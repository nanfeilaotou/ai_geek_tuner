using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.AI.Providers.Transport
{
    /// <summary>
    /// Ollama 原生协议的 Provider foundation 客户端：
    /// 只服务 profile 的模型发现（/api/tags）与连接测试（/api/chat 最小探测）。
    /// 绝不替代或重写现有生产 OllamaService / OllamaConnectionService——
    /// M5.0 里现有 Diagnosis / Session Analysis 继续走旧路径。
    /// </summary>
    public interface IOllamaNativeClient
    {
        Task<AiModelDiscoveryResult> ListModelsAsync(
            string baseUrl,
            CancellationToken cancellationToken = default);

        Task<AiConnectionTestResult> ProbeChatAsync(
            string baseUrl,
            string modelId,
            CancellationToken cancellationToken = default);

        Task<AiStructuredOutputProbeResult> ProbeStructuredOutputAsync(
            string baseUrl,
            string modelId,
            CancellationToken cancellationToken = default);
    }

    public sealed class OllamaNativeClient : IOllamaNativeClient
    {
        private const int DefaultTimeoutSeconds = 15;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly int _timeoutSeconds;

        public OllamaNativeClient(HttpClient httpClient, int timeoutSeconds = DefaultTimeoutSeconds)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _timeoutSeconds = timeoutSeconds;
        }

        public async Task<AiModelDiscoveryResult> ListModelsAsync(
            string baseUrl,
            CancellationToken cancellationToken = default)
        {
            if (!AiEndpointUriBuilder.TryCreateApiPathUri(baseUrl, "api/tags", out var uri))
            {
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "服务地址无效。",
                    Array.Empty<string>());
            }

            try
            {
                using var linked = CreateLinkedSource(cancellationToken);
                using var response = await _httpClient.GetAsync(uri, linked.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return new AiModelDiscoveryResult(
                        AiModelDiscoveryStatus.ModelsUnavailable,
                        $"/api/tags 请求失败（HTTP {(int)response.StatusCode}）。可以手动添加模型 ID。",
                        Array.Empty<string>());
                }

                var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(JsonOptions, linked.Token);
                if (tags?.Models is null)
                {
                    return new AiModelDiscoveryResult(
                        AiModelDiscoveryStatus.ModelsUnavailable,
                        "/api/tags 响应格式无法解析。可以手动添加模型 ID。",
                        Array.Empty<string>());
                }

                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ModelsDiscovered,
                    $"发现 {tags.Models.Count} 个模型。",
                    OpenAiCompatibleClient.NormalizeModelIds(tags.Models.Select(model => model.Name)));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider models fetch");
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "连接超时，请确认 Ollama 服务正在运行。",
                    Array.Empty<string>());
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider models fetch");
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "无法连接到 Ollama，请确认服务正在运行、地址正确。",
                    Array.Empty<string>());
            }
            catch (JsonException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider models fetch");
                return new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ModelsUnavailable,
                    "/api/tags 响应格式无法解析。可以手动添加模型 ID。",
                    Array.Empty<string>());
            }
        }

        public async Task<AiConnectionTestResult> ProbeChatAsync(
            string baseUrl,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            if (!AiEndpointUriBuilder.TryCreateApiPathUri(baseUrl, "api/chat", out var uri))
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

            var payload = new OllamaChatProbeRequest
            {
                Model = normalizedModel,
                Messages = new List<OllamaChatProbeMessage>
                {
                    new() { Role = "user", Content = "ping" }
                },
                Stream = false,
                Options = new OllamaChatProbeOptions { NumPredict = 16 }
            };

            try
            {
                using var linked = CreateLinkedSource(cancellationToken);
                using var response = await _httpClient.PostAsJsonAsync(uri, payload, JsonOptions, linked.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.NotFound => new AiConnectionTestResult(
                            AiConnectionTestStatus.ModelRejected,
                            "请求的模型不存在（HTTP 404）。请确认已执行 ollama pull。"),
                        _ => new AiConnectionTestResult(
                            AiConnectionTestStatus.ConnectionUnavailable,
                            $"Ollama 返回了错误状态码 HTTP {(int)response.StatusCode}。")
                    };
                }

                try
                {
                    var envelope = await response.Content.ReadFromJsonAsync<OllamaChatProbeResponse>(JsonOptions, linked.Token);
                    if (string.IsNullOrEmpty(envelope?.Model))
                    {
                        return new AiConnectionTestResult(
                            AiConnectionTestStatus.InvalidResponse,
                            "Ollama 返回了无法解析的响应。");
                    }

                    return new AiConnectionTestResult(
                        AiConnectionTestStatus.Connected,
                        $"连接成功，模型 {normalizedModel} 可用。");
                }
                catch (JsonException exception)
                {
                    ExceptionLogWriter.Write(exception, "Ollama provider chat probe");
                    return new AiConnectionTestResult(
                        AiConnectionTestStatus.InvalidResponse,
                        "Ollama 返回了无法解析的响应。");
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider chat probe");
                return new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "连接超时，请确认 Ollama 服务正在运行。");
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider chat probe");
                return new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "无法连接到 Ollama，请确认服务正在运行、地址正确。");
            }
        }

        public async Task<AiStructuredOutputProbeResult> ProbeStructuredOutputAsync(
            string baseUrl,
            string modelId,
            CancellationToken cancellationToken = default)
        {
            if (!AiEndpointUriBuilder.TryCreateApiPathUri(baseUrl, "api/chat", out var uri))
            {
                return new AiStructuredOutputProbeResult(false, "服务地址无效。");
            }

            var normalizedModel = AiProviderModelId.Normalize(modelId);
            if (normalizedModel is null)
            {
                return new AiStructuredOutputProbeResult(false, "未指定模型，无法探测结构化输出能力。");
            }

            var payload = new OllamaChatProbeRequest
            {
                Model = normalizedModel,
                Messages = new List<OllamaChatProbeMessage>
                {
                    new() { Role = "user", Content = "ping" }
                },
                Stream = false,
                Options = new OllamaChatProbeOptions { NumPredict = 16 },
                Format = new
                {
                    type = "object",
                    properties = new
                    {
                        ok = new { type = "boolean" }
                    },
                    required = new[] { "ok" }
                }
            };

            try
            {
                using var linked = CreateLinkedSource(cancellationToken);
                using var response = await _httpClient.PostAsJsonAsync(uri, payload, JsonOptions, linked.Token);
                if (response.IsSuccessStatusCode)
                {
                    return new AiStructuredOutputProbeResult(true, "该 Ollama 端点支持原生 JSON Schema format。");
                }

                return new AiStructuredOutputProbeResult(
                    false,
                    $"端点未接受 JSON Schema format（HTTP {(int)response.StatusCode}）。这只是一个能力探测，不影响 Provider 配置。");
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider structured probe");
                return new AiStructuredOutputProbeResult(false, "探测超时。这只是一个能力探测，不影响 Provider 配置。");
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama provider structured probe");
                return new AiStructuredOutputProbeResult(false, "无法连接到 Ollama。这只是一个能力探测，不影响 Provider 配置。");
            }
        }

        private CancellationTokenSource CreateLinkedSource(CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            return linked;
        }

        private sealed class OllamaTagsResponse
        {
            [property: JsonPropertyName("models")]
            public List<OllamaTagModel>? Models { get; set; }
        }

        private sealed class OllamaTagModel
        {
            [property: JsonPropertyName("name")]
            public string? Name { get; set; }
        }

        private sealed class OllamaChatProbeRequest
        {
            [property: JsonPropertyName("model")]
            public string Model { get; set; } = string.Empty;

            [property: JsonPropertyName("messages")]
            public List<OllamaChatProbeMessage> Messages { get; set; } = new();

            [property: JsonPropertyName("stream")]
            public bool Stream { get; set; }

            [property: JsonPropertyName("options")]
            public OllamaChatProbeOptions? Options { get; set; }

            [property: JsonPropertyName("format")]
            public object? Format { get; set; }
        }

        private sealed class OllamaChatProbeMessage
        {
            [property: JsonPropertyName("role")]
            public string Role { get; set; } = "user";

            [property: JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class OllamaChatProbeOptions
        {
            [property: JsonPropertyName("num_predict")]
            public int NumPredict { get; set; }
        }

        private sealed class OllamaChatProbeResponse
        {
            [property: JsonPropertyName("model")]
            public string? Model { get; set; }

            [property: JsonPropertyName("done")]
            public bool? Done { get; set; }
        }
    }
}
