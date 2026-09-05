using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers.Runtime
{
    /// <summary>
    /// Ollama 原生 /api/chat 的 provider-aware transport（Gate F）。
    /// 结构化输出映射（Gate G，按 Profile 的 <see cref="AiStructuredOutputMode"/>）：
    /// NativeSchema / OpenAiJsonSchema（明确映射）→ 原生 format（schema 或 json）；
    /// JsonObject → format:"json"；PromptOnly → 绝不发送 format。
    /// 超时来自快照 TimeoutSeconds；错误一律映射为脱敏的 <see cref="AiRuntimeException"/>。
    /// </summary>
    public sealed class OllamaNativeChatTransport : IAiChatTransport
    {
        private readonly HttpClient _httpClient;

        public OllamaNativeChatTransport(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            // 与现有 OllamaService 一致：关闭 HttpClient 默认 100s 超时，
            // 让快照里的 TimeoutSeconds（链接 CTS）成为唯一生效超时。
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public async Task<AiChatResponse> SendAsync(
            AiChatRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var runtime = request.Runtime;
            if (runtime.ProviderKind != AiProviderKind.OllamaNative)
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ProtocolError,
                    "Ollama 原生 transport 被要求处理非 Ollama 协议的请求。");
            }

            if (!AiEndpointUriBuilder.TryCreateApiPathUri(runtime.BaseUrl, "api/chat", out var uri))
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ProtocolError,
                    "当前 AI 服务提供方的服务地址无效，无法发起请求。");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(runtime.TimeoutSeconds));

            var payload = BuildPayload(request);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(uri, content, timeoutSource.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiRuntimeException(AiRuntimeError.Timeout, "AI 请求超时。", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ConnectionUnavailable,
                    "无法连接到当前 AI 服务提供方。",
                    exception);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw MapHttpError(response.StatusCode, runtime.ModelId);
                }

                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AiRuntimeException(AiRuntimeError.Timeout, "AI 请求超时。", exception);
                }

                string? messageContent;
                try
                {
                    using var document = JsonDocument.Parse(body);
                    messageContent = document.RootElement
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();
                }
                catch (JsonException exception)
                {
                    throw new AiRuntimeException(
                        AiRuntimeError.InvalidResponse,
                        "AI 服务返回了无法解析的响应。",
                        exception);
                }
                catch (KeyNotFoundException exception)
                {
                    throw new AiRuntimeException(
                        AiRuntimeError.InvalidResponse,
                        "AI 服务返回了无法识别的响应（缺少 message.content）。",
                        exception);
                }

                if (string.IsNullOrWhiteSpace(messageContent))
                {
                    throw new AiRuntimeException(
                        AiRuntimeError.InvalidResponse,
                        "AI 服务返回了空响应。");
                }

                return new AiChatResponse(messageContent.Trim(), runtime.ModelId);
            }
        }

        /// <summary>HTTP 状态码 → 脱敏错误；绝不回显响应体原文。</summary>
        private static AiRuntimeException MapHttpError(
            System.Net.HttpStatusCode statusCode,
            string modelId)
        {
            return statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden => new AiRuntimeException(
                        AiRuntimeError.AuthenticationFailed,
                        "AI 服务认证失败，请检查 Provider API Key。"),
                System.Net.HttpStatusCode.NotFound => new AiRuntimeException(
                    AiRuntimeError.ModelRejected,
                    "当前 Provider 无法使用模型 " + modelId + "，请检查默认模型配置。"),
                System.Net.HttpStatusCode.BadRequest => new AiRuntimeException(
                    AiRuntimeError.ModelRejected,
                    "当前 Provider 拒绝了模型请求（HTTP 400）。请检查默认模型配置。"),
                _ => new AiRuntimeException(
                    AiRuntimeError.ConnectionUnavailable,
                    "AI 服务返回了错误状态码 HTTP " + (int)statusCode + "。")
            };
        }

        internal static string BuildPayload(AiChatRequest request)
        {
            var runtime = request.Runtime;
            var options = request.Options;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", runtime.ModelId);

                writer.WriteStartArray("messages");
                foreach (var message in request.Messages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", message.Role);
                    writer.WriteString("content", message.Content);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteBoolean("stream", false);

                // Qwen3 等思考型模型：结构化场景显式关闭 thinking（与现有生产请求一致）。
                if (options.Think is not null)
                {
                    writer.WriteBoolean("think", options.Think.Value);
                }

                WriteFormat(writer, runtime, request.StructuredOutput);

                var hasOptions = options.Temperature is not null || options.MaxOutputTokens is not null;
                if (hasOptions)
                {
                    writer.WriteStartObject("options");
                    if (options.Temperature is not null)
                    {
                        writer.WriteNumber("temperature", options.Temperature.Value);
                    }

                    if (options.MaxOutputTokens is not null)
                    {
                        writer.WriteNumber("num_predict", options.MaxOutputTokens.Value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Ollama 原生 format 字段映射（Gate G）。PromptOnly 永远不发送；
        /// OpenAiJsonSchema 模式在 Ollama 原生协议上做“明确映射”（同一份 schema 文档
        /// 走原生 format），绝不静默降级为错误协议。
        /// </summary>
        private static void WriteFormat(
            Utf8JsonWriter writer,
            AiRuntimeSnapshot runtime,
            AiStructuredOutputRequest structuredOutput)
        {
            if (structuredOutput.Intent == AiStructuredOutputIntent.None)
            {
                return;
            }

            if (runtime.StructuredOutputMode == AiStructuredOutputMode.PromptOnly)
            {
                return;
            }

            if (runtime.StructuredOutputMode == AiStructuredOutputMode.JsonObject)
            {
                // Gate G：JsonObject 模式只承诺 JSON mode，不承担 schema 约束。
                writer.WriteString("format", "json");
                return;
            }

            if (structuredOutput.Intent == AiStructuredOutputIntent.JsonObject
                || string.IsNullOrWhiteSpace(structuredOutput.JsonSchema))
            {
                writer.WriteString("format", "json");
                return;
            }

            writer.WritePropertyName("format");
            writer.WriteRawValue(structuredOutput.JsonSchema);
        }
    }
}
