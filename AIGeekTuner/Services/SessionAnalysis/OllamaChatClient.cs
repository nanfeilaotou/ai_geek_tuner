using AIGeekTuner.Services.Telemetry.Recording;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Configuration;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 生产实现：POST /api/chat（stream=false）。format 支持完整 JSON Schema；
    /// 超时使用 OllamaOptions.TimeoutSeconds（链接 CTS），取消透传。
    /// </summary>
    public sealed class OllamaChatClient : IOllamaChatClient
    {
        private readonly HttpClient _httpClient;
        private readonly Func<OllamaOptions> _optionsProvider;

        public OllamaChatClient(HttpClient httpClient, Func<OllamaOptions> optionsProvider)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public async Task<string> ChatAsync(
            string systemPrompt,
            string userPrompt,
            string? formatJsonSchema,
            bool? think,
            CancellationToken cancellationToken)
        {
            var options = _optionsProvider();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            var payload = BuildPayload(options, systemPrompt, userPrompt, formatJsonSchema, think);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var endpoint = options.BaseUrl.TrimEnd('/') + "/api/chat";
            using var response = await _httpClient.PostAsync(endpoint, content, linked.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
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



