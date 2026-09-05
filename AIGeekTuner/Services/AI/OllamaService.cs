using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Diagnosis;

namespace AIGeekTuner.Services.AI
{
    public sealed class OllamaService : IAiService
    {
        private readonly HttpClient _httpClient;
        private readonly Func<OllamaOptions> _optionsSource;
        private readonly DiagnosticResultParser _resultParser;

        public OllamaService(
            HttpClient httpClient,
            OllamaOptions options,
            DiagnosticResultParser? resultParser = null)
            : this(httpClient, () => options, resultParser)
        {
        }

        /// <summary>
        /// 以“配置工厂”构造：每次请求开始时调用一次并全程使用该次返回的快照，
        /// 因此设置保存后下一次请求立即生效，而进行中的请求不受影响。
        /// </summary>
        public OllamaService(
            HttpClient httpClient,
            Func<OllamaOptions> optionsSource,
            DiagnosticResultParser? resultParser = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            // Ollama requests use the linked CancellationToken timeout below. Disable
            // HttpClient's 100-second default so the per-request TimeoutSeconds remains
            // the single effective timeout for larger local models.
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _optionsSource = optionsSource ?? throw new ArgumentNullException(nameof(optionsSource));
            _resultParser = resultParser ?? new DiagnosticResultParser();
        }

        public async Task<bool> IsAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var options = ResolveOptions();
                return await ExecuteWithTimeoutAsync(options.TimeoutSeconds, async token =>
                {
                    using var response = await _httpClient.GetAsync(
                        CreateEndpointUri(options.BaseUrl, "api/tags"),
                        token);
                    return response.IsSuccessStatusCode;
                }, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            CancellationToken cancellationToken = default)
        {
            return await GetDiagnosticResultAsync(
                systemPrompt,
                userContext,
                ResolveOptions(),
                cancellationToken);
        }

        /// <summary>
        /// 由调用方显式提供本次使用的配置快照；
        /// 修复重试等内部后续请求同样使用这一份。
        /// </summary>
        public async Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            OllamaOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                throw new ArgumentException("System Prompt 不能为空。", nameof(systemPrompt));
            }

            if (string.IsNullOrWhiteSpace(userContext))
            {
                throw new ArgumentException("用户上下文不能为空。", nameof(userContext));
            }

            var baseMessages = new ChatMessage[]
            {
                new("system", systemPrompt),
                new("user", userContext)
            };

            var firstResponse = await SendMessagesAsync(
                baseMessages,
                options,
                cancellationToken);

            try
            {
                return _resultParser.Parse(firstResponse);
            }
            catch (DiagnosticResultParsingException firstError)
            {
                // 只有“模型成功返回文本但结构无效”才做唯一一次修复重试；
                // HTTP 错误、超时、取消等异常不会进入本分支，直接向上传播。
                var repairResponse = await SendMessagesAsync(
                [
                    .. baseMessages,
                    new ChatMessage("assistant", firstResponse),
                    new ChatMessage("user", Diagnosis.DiagnosisRepairInstruction.Build(firstError))
                ],
                options,
                cancellationToken);

                try
                {
                    return _resultParser.Parse(repairResponse);
                }
                catch (DiagnosticResultParsingException secondError)
                {
                    throw new DiagnosticResultParsingException(
                        $"模型两次输出均无法解析为诊断结果。首次错误：{firstError.Message}；修复后错误：{secondError.Message}",
                        secondError);
                }
            }
        }

        private async Task<string> SendMessagesAsync(
            IReadOnlyList<ChatMessage> messages,
            OllamaOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                return await ExecuteWithTimeoutAsync(options.TimeoutSeconds, async token =>
                {
                    var request = new ChatRequest(
                        options.ModelName,
                        messages,
                        Stream: false,
                        Think: false,
                        Options: new ChatOptions(Temperature: 0.2),
                        Format: options.UseJsonFormat ? "json" : null);

                    using var response = await _httpClient.PostAsJsonAsync(
                        CreateEndpointUri(options.BaseUrl, "api/chat"),
                        request,
                        token);

                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync(token);
                        throw new OllamaServiceException(
                            $"Ollama 请求失败，HTTP 状态码 {(int)response.StatusCode}：{LimitErrorText(error)}");
                    }

                    var result = await response.Content.ReadFromJsonAsync<ChatResponse>(
                        cancellationToken: token);

                    if (string.IsNullOrWhiteSpace(result?.Message?.Content))
                    {
                        throw new OllamaServiceException("Ollama 返回了空响应或无法识别的响应。");
                    }

                    return result.Message.Content.Trim();
                }, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new OllamaServiceException(
                    $"无法连接本地 Ollama 服务：{ResolveOptions().BaseUrl}",
                    exception);
            }
            catch (JsonException exception)
            {
                throw new OllamaServiceException(
                    "Ollama 返回了无法解析的聊天响应。",
                    exception);
            }
        }

        private static async Task<T> ExecuteWithTimeoutAsync<T>(
            int timeoutSeconds,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                return await operation(timeoutSource.Token);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Ollama 请求在 {timeoutSeconds} 秒后超时。",
                    exception);
            }
        }

        private OllamaOptions ResolveOptions()
        {
            var options = _optionsSource();
            ArgumentNullException.ThrowIfNull(options);
            return options;
        }

        private Uri CreateEndpointUri(string baseUrl, string relativePath)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new OllamaServiceException(
                    $"Ollama 服务地址无效：{baseUrl}");
            }

            var normalized = baseUrl.EndsWith('/')
                ? baseUrl
                : $"{baseUrl}/";
            return new(new Uri(normalized), relativePath);
        }

        private static string LimitErrorText(string error)
        {
            const int maxLength = 500;
            var normalized = error.Trim();
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength];
        }

        private sealed record ChatRequest(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
            [property: JsonPropertyName("stream")] bool Stream,
            [property: JsonPropertyName("think")] bool Think,
            [property: JsonPropertyName("options")] ChatOptions Options,
            // 关闭 JSON mode 时整个 format 字段从请求体中省略，而不是发送 null。
            [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [property: JsonPropertyName("format")] string? Format = null);

        private sealed record ChatOptions(
            [property: JsonPropertyName("temperature")] double Temperature);

        private sealed record ChatMessage(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content);

        private sealed class ChatResponse
        {
            [JsonPropertyName("message")]
            public ChatMessage? Message { get; init; }
        }
    }
}
