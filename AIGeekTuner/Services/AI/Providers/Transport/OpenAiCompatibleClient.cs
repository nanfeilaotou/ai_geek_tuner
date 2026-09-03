using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.AI.Providers.Transport
{
    /// <summary>OpenAI 兼容传输的抽象：models 发现 / 最小 chat 探测 / 结构化输出能力探测。</summary>
    public interface IOpenAiCompatibleClient
    {
        Task<AiModelDiscoveryResult> ListModelsAsync(
            string baseUrl,
            string? apiKey,
            CancellationToken cancellationToken = default);

        Task<AiConnectionTestResult> ProbeChatAsync(
            string baseUrl,
            string? apiKey,
            string modelId,
            CancellationToken cancellationToken = default);

        Task<AiStructuredOutputProbeResult> ProbeStructuredOutputAsync(
            string baseUrl,
            string? apiKey,
            string modelId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 用 typed HttpClient DTO 实现的轻量 OpenAI 兼容客户端（不引入官方 SDK）。
    /// 鉴权：有 Key 才加 Authorization: Bearer；没有 Key 就不带该头——
    /// LM Studio 等本地服务不要求 Key，绝不能因为缺 Key 判定 Profile 无效。
    /// 所有面向用户的消息都经过脱敏：不包含 Authorization 头、请求体原文或响应体原文。
    /// </summary>
    public sealed class OpenAiCompatibleClient : IOpenAiCompatibleClient
    {
        private const int DefaultTimeoutSeconds = 15;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly int _timeoutSeconds;

        public OpenAiCompatibleClient(HttpClient httpClient, int timeoutSeconds = DefaultTimeoutSeconds)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _timeoutSeconds = timeoutSeconds;
        }

        public async Task<AiModelDiscoveryResult> ListModelsAsync(
            string baseUrl,
            string? apiKey,
            CancellationToken cancellationToken = default)
        {
            if (!AiEndpointUriBuilder.TryCreateModelsUri(baseUrl, out var uri))
            {
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "服务地址无效。",
                    Array.Empty<string>());
            }

            try
            {
                using var linked = CreateLinkedSource(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                ApplyAuthorization(request, apiKey);
                using var response = await _httpClient.SendAsync(request, linked.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return new AiModelDiscoveryResult(
                        AiModelDiscoveryStatus.AuthenticationFailed,
                        $"认证失败（HTTP {(int)response.StatusCode}），请检查 API Key。",
                        Array.Empty<string>());
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 404 / 其他非 2xx：只是 /models 不可用，不代表 Provider 不可用。
                    return new AiModelDiscoveryResult(
                        AiModelDiscoveryStatus.ModelsUnavailable,
                        $"/models 请求失败（HTTP {(int)response.StatusCode}）。可以手动添加模型 ID。",
                        Array.Empty<string>());
                }

                var payload = await response.Content.ReadFromJsonAsync<OpenAiModelListResponse>(JsonOptions, linked.Token);
                if (payload?.Data is null)
                {
                    return new AiModelDiscoveryResult(
                        AiModelDiscoveryStatus.ModelsUnavailable,
                        "/models 响应格式无法解析。可以手动添加模型 ID。",
                        Array.Empty<string>());
                }

                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ModelsDiscovered,
                    $"发现 {payload.Data.Count} 个模型。",
                    NormalizeModelIds(payload.Data.Select(model => model.Id)));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible models fetch");
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "连接超时，请确认服务正在运行。",
                    Array.Empty<string>());
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible models fetch");
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "无法连接到服务，请确认服务正在运行、地址正确。",
                    Array.Empty<string>());
            }
            catch (JsonException exception)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible models fetch");
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ModelsUnavailable,
                    "/models 响应格式无法解析。可以手动添加模型 ID。",
                    Array.Empty<string>());
            }
        }

        public async Task<AiConnectionTestResult> ProbeChatAsync(
            string baseUrl,
            string? apiKey,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            if (!AiEndpointUriBuilder.TryCreateChatCompletionsUri(baseUrl, out var uri))
            {
                return new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "服务地址无效。");
            }

            var normalizedModel = AiProviderModelId.Normalize(modelId);
            if (normalizedModel is null)
            {
                return new AiConnectionTestResult(
                    AiConnectionTestStatus.MissingModel,
                    "未指定模型，无法测试连接。请先选择或手动添加模型。");
            }

            try
            {
                using var linked = CreateLinkedSource(cancellationToken);

                var firstResult = await SendChatProbeAsync(
                    uri,
                    apiKey,
                    normalizedModel,
                    useLegacyMaxTokens: true,
                    linked.Token);

                // 少数端点只认 max_completion_tokens：按返回的错误类型补一次等价探测。
                if (firstResult.RetryWithMaxCompletionTokens)
                {
                    firstResult = await SendChatProbeAsync(
                        uri,
                        apiKey,
                        normalizedModel,
                        useLegacyMaxTokens: false,
                        linked.Token);
                }

                if (firstResult.IsSuccess)
                {
                    return new AiConnectionTestResult(
                        AiConnectionTestStatus.Connected,
                        $"连接成功，模型 {normalizedModel} 可用。");
                }

                return firstResult.Result!;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible chat probe");
                return new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "连接超时，请确认服务正在运行。");
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible chat probe");
                return new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "无法连接到服务，请确认服务正在运行、地址正确。");
            }
        }

        public async Task<AiStructuredOutputProbeResult> ProbeStructuredOutputAsync(
            string baseUrl,
            string? apiKey,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            if (!AiEndpointUriBuilder.TryCreateChatCompletionsUri(baseUrl, out var uri))
            {
                return new AiStructuredOutputProbeResult(false, "服务地址无效。");
            }

            var normalizedModel = AiProviderModelId.Normalize(modelId);
            if (normalizedModel is null)
            {
                return new AiStructuredOutputProbeResult(false, "未指定模型，无法探测结构化输出能力。");
            }

            var payload = new OpenAiChatProbeRequest
            {
                Model = normalizedModel,
                Messages = new List<OpenAiChatProbeMessage>
                {
                    new() { Role = "user", Content = "ping" }
                },
                Stream = false,
                MaxTokens = 16,
                ResponseFormat = new OpenAiJsonSchemaResponseFormat
                {
                    JsonSchema = new OpenAiJsonSchemaDefinition
                    {
                        Name = "probe",
                        Strict = true,
                        Schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                ok = new { type = "boolean" }
                            },
                            required = new[] { "ok" },
                            additionalProperties = false
                        }
                    }
                }
            };

            try
            {
                using var linked = CreateLinkedSource(cancellationToken);
                using var response = await PostJsonAsync(uri, apiKey, payload, linked.Token);
                if (response.IsSuccessStatusCode)
                {
                    return new AiStructuredOutputProbeResult(true, "该端点支持 response_format: json_schema。");
                }

                return new AiStructuredOutputProbeResult(
                    false,
                    $"端点未接受 json_schema（HTTP {(int)response.StatusCode}）。这只是一个能力探测，不影响 Provider 配置。");
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible structured probe");
                return new AiStructuredOutputProbeResult(false, "探测超时。这只是一个能力探测，不影响 Provider 配置。");
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "OpenAI-compatible structured probe");
                return new AiStructuredOutputProbeResult(false, "无法连接到服务。这只是一个能力探测，不影响 Provider 配置。");
            }
        }

        private async Task<(bool IsSuccess, bool RetryWithMaxCompletionTokens, AiConnectionTestResult? Result)> SendChatProbeAsync(
            Uri uri,
            string? apiKey,
            string modelId,
            bool useLegacyMaxTokens,
            CancellationToken cancellationToken)
        {
            var payload = new OpenAiChatProbeRequest
            {
                Model = modelId,
                Messages = new List<OpenAiChatProbeMessage>
                {
                    new() { Role = "user", Content = "ping" }
                },
                Stream = false,
                MaxTokens = useLegacyMaxTokens ? 16 : null,
                MaxCompletionTokens = useLegacyMaxTokens ? null : 16
            };

            using var response = await PostJsonAsync(uri, apiKey, payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var envelope = await response.Content.ReadFromJsonAsync<OpenAiChatProbeResponse>(JsonOptions, cancellationToken);
                    if (envelope?.Choices is { Count: > 0 })
                    {
                        return (true, false, null);
                    }

                    return (false, false, new AiConnectionTestResult(
                        AiConnectionTestStatus.InvalidResponse,
                        "服务返回了无法解析的响应（缺少 choices）。"));
                }
                catch (JsonException exception)
                {
                    ExceptionLogWriter.Write(exception, "OpenAI-compatible chat probe");
                    return (false, false, new AiConnectionTestResult(
                        AiConnectionTestStatus.InvalidResponse,
                        "服务返回了无法解析的响应。"));
                }
            }

            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.Unauthorized:
                case System.Net.HttpStatusCode.Forbidden:
                    return (false, false, new AiConnectionTestResult(
                        AiConnectionTestStatus.AuthenticationFailed,
                        $"认证失败（HTTP {(int)response.StatusCode}），请检查 API Key。"));
                case System.Net.HttpStatusCode.NotFound:
                    return (false, false, new AiConnectionTestResult(
                        AiConnectionTestStatus.ModelRejected,
                        "请求的模型或端点不存在（HTTP 404）。请确认模型 ID 正确、服务地址以 /v1 结尾（如适用）。"));
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var errorType = await ReadErrorTypeAsync(response, cancellationToken);
                var retry = errorType is not null
                    && errorType.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase);
                if (retry)
                {
                    return (false, true, null);
                }

                return (false, false, new AiConnectionTestResult(
                    AiConnectionTestStatus.ModelRejected,
                    "服务拒绝了测试请求（HTTP 400）。请确认模型 ID 正确。"));
            }

            return (false, false, new AiConnectionTestResult(
                AiConnectionTestStatus.ConnectionUnavailable,
                $"服务返回了错误状态码 HTTP {(int)response.StatusCode}。"));
        }

        private async Task<HttpResponseMessage> PostJsonAsync(
            Uri uri,
            string? apiKey,
            OpenAiChatProbeRequest payload,
            CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            ApplyAuthorization(request, apiKey);
            return await _httpClient.SendAsync(request, cancellationToken);
        }

        /// <summary>只提取 error.type / error.code 作为脱敏诊断；绝不回显错误消息原文或请求体。</summary>
        private static async Task<string?> ReadErrorTypeAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("error", out var error)
                    || error.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (error.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String)
                {
                    return type.GetString();
                }

                if (error.TryGetProperty("code", out var code)
                    && code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyAuthorization(HttpRequestMessage request, string? apiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        private CancellationTokenSource CreateLinkedSource(CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            return linked;
        }

        /// <summary>trim → 去空 → 去重（OrdinalIgnoreCase）→ 稳定排序（Ordinal）。</summary>
        internal static IReadOnlyList<string> NormalizeModelIds(IEnumerable<string?> rawIds)
        {
            return rawIds
                .Select(id => id?.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        private sealed class OpenAiModelListResponse
        {
            [property: JsonPropertyName("data")]
            public List<OpenAiModelEntry>? Data { get; set; }
        }

        private sealed class OpenAiModelEntry
        {
            [property: JsonPropertyName("id")]
            public string? Id { get; set; }
        }

        private sealed class OpenAiChatProbeRequest
        {
            [property: JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [property: JsonPropertyName("messages")]
            public List<OpenAiChatProbeMessage> Messages { get; set; } = new();

            [property: JsonPropertyName("stream")]
            public bool Stream { get; set; }

            [property: JsonPropertyName("max_tokens")]
            public int? MaxTokens { get; set; }

            [property: JsonPropertyName("max_completion_tokens")]
            public int? MaxCompletionTokens { get; set; }

            [property: JsonPropertyName("response_format")]
            public OpenAiJsonSchemaResponseFormat? ResponseFormat { get; set; }
        }

        private sealed class OpenAiChatProbeMessage
        {
            [property: JsonPropertyName("role")]
            public string Role { get; set; } = "user";

            [property: JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class OpenAiChatProbeResponse
        {
            [property: JsonPropertyName("choices")]
            public List<JsonElement>? Choices { get; set; }
        }

        private sealed class OpenAiJsonSchemaResponseFormat
        {
            [property: JsonPropertyName("type")]
            public string Type { get; set; } = "json_schema";

            [property: JsonPropertyName("json_schema")]
            public OpenAiJsonSchemaDefinition JsonSchema { get; set; } = new();
        }

        private sealed class OpenAiJsonSchemaDefinition
        {
            [property: JsonPropertyName("name")]
            public string Name { get; set; } = "probe";

            [property: JsonPropertyName("strict")]
            public bool Strict { get; set; } = true;

            [property: JsonPropertyName("schema")]
            public object Schema { get; set; } = new();
        }
    }
}
