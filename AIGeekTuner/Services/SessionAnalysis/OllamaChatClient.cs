using System.Net.Http;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Telemetry.Recording;
using System.IO;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Configuration;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 生产 chat 客户端（V2-M5.1B 起 provider-aware）：
    /// 传入运行时快照时经 <see cref="IAiChatTransport"/> 发送（模型/地址/超时全部来自快照，
    /// 结构化输出映射由 transport 按 Provider 协议完成）；未传快照时保持 legacy
    /// Ollama /api/chat 行为（兼容旧测试）。format 支持完整 JSON Schema。
    /// </summary>
    public sealed class OllamaChatClient : IOllamaChatClient
    {
        private readonly HttpClient _httpClient;
        private readonly Func<OllamaOptions> _optionsProvider;
        private readonly IAiChatTransport? _transport;

        public OllamaChatClient(
            HttpClient httpClient,
            Func<OllamaOptions> optionsProvider,
            IAiChatTransport? transport = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _transport = transport;
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public async Task<string> ChatAsync(
            AiRuntimeSnapshot? runtime,
            string systemPrompt,
            string userPrompt,
            string? formatJsonSchema,
            bool? think,
            CancellationToken cancellationToken)
        {
            // V2-M5.1B：有运行时快照就走 provider-aware transport（Gate D/F/G）。
            if (runtime is not null && _transport is not null)
            {
                var messages = new[]
                {
                    new AiChatMessage("system", systemPrompt),
                    new AiChatMessage("user", userPrompt)
                };
                var structuredOutput = string.IsNullOrWhiteSpace(formatJsonSchema)
                    ? AiStructuredOutputRequest.JsonObject
                    : AiStructuredOutputRequest.WithSchema(formatJsonSchema);
                var response = await _transport.SendAsync(
                    new AiChatRequest(
                        runtime,
                        messages,
                        structuredOutput,
                        // 与 legacy 行为一致：num_predict 2048；think 由调用方决定。
                        new AiChatRuntimeOptions(Think: think, MaxOutputTokens: 2048)),
                    cancellationToken);
                return response.Content;
            }

            var options = _optionsProvider();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            var payload = BuildPayload(options, systemPrompt, userPrompt, formatJsonSchema, think);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/chat";
            using var responseLegacy = await _httpClient.PostAsync(endpoint, content, linked.Token)
                .ConfigureAwait(false);
            responseLegacy.EnsureSuccessStatusCode();

            var json = await responseLegacy.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
        }

        private static string BuildPayload(
            OllamaOptions options,
            string systemPrompt,
            string userPrompt,
            string? formatJsonSchema,
            bool? think)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", options.ModelName);
                writer.WriteStartArray("messages");
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", systemPrompt);
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", userPrompt);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteBoolean("stream", false);

                // Qwen3 等思考型模型：结构化解读场景显式关闭 thinking（§2/§3）。
                // 不支持该参数的模型/Ollama 版本会忽略它。
                if (think is not null)
                {
                    writer.WriteBoolean("think", think.Value);
                }

                if (formatJsonSchema is not null)
                {
                    writer.WritePropertyName("format");
                    writer.WriteRawValue(formatJsonSchema);
                }
                else
                {
                    writer.WriteString("format", "json");
                }

                writer.WriteStartObject("options");
                writer.WriteNumber("num_predict", 2048);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
