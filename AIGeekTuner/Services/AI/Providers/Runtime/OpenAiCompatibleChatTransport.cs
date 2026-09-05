using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers.Runtime
{
    /// <summary>
    /// OpenAI 兼容 /chat/completions 的 provider-aware transport（Gate F）。
    /// 复用 M5.0 已验证的端点规则（AiEndpointUriBuilder：绝不自动追加 /v1）与
    /// 鉴权规则（有 Key 才带 Bearer，LM Studio / llama.cpp 无 Key 可用）。
    /// 结构化输出映射（Gate G）：OpenAiJsonSchema → response_format json_schema；
    /// JsonObject → json_object；PromptOnly → 不发送 response_format；
    /// NativeSchema → 明确抛 ProtocolError（绝不偷偷换语义）。
    /// </summary>
    public sealed class OpenAiCompatibleChatTransport : IAiChatTransport
    {
        private readonly HttpClient _httpClient;

        public OpenAiCompatibleChatTransport(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public async Task<AiChatResponse> SendAsync(
            AiChatRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var runtime = request.Runtime;
            if (runtime.ProviderKind != AiProviderKind.OpenAiCompatible)
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ProtocolError,
                    "OpenAI 兼容 transport 被要求处理非 OpenAI 兼容协议的请求。");
            }

            if (!AiEndpointUriBuilder.TryCreateChatCompletionsUri(runtime.BaseUrl, out var uri))
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ProtocolError,
                    "当前 AI 服务提供方的服务地址无效，无法发起请求。");
            }

            // 协议/模式不匹配必须显式失败，绝不静默替换语义（Gate G）。
            if (request.StructuredOutput.Intent != AiStructuredOutputIntent.None
                && runtime.StructuredOutputMode == AiStructuredOutputMode.NativeSchema)
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ProtocolError,
                    "当前 Provider 的结构化输出模式 NativeSchema 与 OpenAI 兼容协议不匹配。"
                    + "请在设置中把该 Provider 的结构化输出模式调整为 OpenAiJsonSchema / JsonObject / PromptOnly。");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(runtime.TimeoutSeconds));

            var payload = BuildPayload(request);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = content
            };
            if (!string.IsNullOrWhiteSpace(runtime.ApiKey))
            {
                httpRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", runtime.ApiKey);
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, timeoutSource.Token);
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
                    var root = document.RootElement;
                    messageContent = root
                        .GetProperty("choices")
                        .EnumerateArray()
                        .FirstOrDefault()
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
                catch (InvalidOperationException exception)
                {
                    throw new AiRuntimeException(
                        AiRuntimeError.InvalidResponse,
                        "AI 服务返回了无法识别的响应（缺少 choices[0].message.content）。",
                        exception);
                }
                catch (KeyNotFoundException exception)
                {
                    throw new AiRuntimeException(
                        AiRuntimeError.InvalidResponse,
                        "AI 服务返回了无法识别的响应（缺少 choices[0].message.content）。",
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

                if (options.Temperature is not null)
                {
                    writer.WriteNumber("temperature", options.Temperature.Value);
                }

                if (options.MaxOutputTokens is not null)
                {
                    writer.WriteNumber("max_tokens", options.MaxOutputTokens.Value);
                }

                WriteResponseFormat(writer, runtime, request.StructuredOutput);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// response_format 映射（Gate G）。PromptOnly 永远不发送；
        /// JsonObject 意图在最保守的模式下也可以映射为 json_object；
        /// JsonSchema 意图 + OpenAiJsonSchema 模式 → 复用 M5.0 探测验证过的
        /// { type: "json_schema", json_schema: { name, strict, schema } } 形态。
        /// </summary>
        private static void WriteResponseFormat(
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
                WriteJsonObjectFormat(writer);
                return;
            }

            // 到这里只能是 OpenAiJsonSchema 模式（NativeSchema 已在入口显式拒绝）。
            if (structuredOutput.Intent == AiStructuredOutputIntent.JsonObject
                || string.IsNullOrWhiteSpace(structuredOutput.JsonSchema))
            {
                WriteJsonObjectFormat(writer);
                return;
            }

            writer.WriteStartObject("response_format");
            writer.WriteString("type", "json_schema");
            writer.WriteStartObject("json_schema");
            writer.WriteString("name", "response");
            writer.WriteBoolean("strict", true);
            writer.WritePropertyName("schema");
            writer.WriteRawValue(structuredOutput.JsonSchema);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static void WriteJsonObjectFormat(Utf8JsonWriter writer)
        {
            writer.WriteStartObject("response_format");
            writer.WriteString("type", "json_object");
            writer.WriteEndObject();
        }
    }
}
