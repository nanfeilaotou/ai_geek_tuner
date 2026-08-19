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
        private readonly Uri _baseUri;
        private readonly OllamaOptions _options;
        private readonly DiagnosticResultParser _resultParser;

        public OllamaService(
            HttpClient httpClient,
            OllamaOptions options,
            DiagnosticResultParser? resultParser = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            // Ollama requests use the linked CancellationToken timeout below. Disable
            // HttpClient's 100-second default so OllamaOptions.TimeoutSeconds remains
            // the single effective timeout for larger local models.
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _resultParser = resultParser ?? new DiagnosticResultParser();
            _baseUri = ValidateAndCreateBaseUri(options);
        }

        public async Task<bool> IsAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await ExecuteWithTimeoutAsync(async token =>
                {
                    using var response = await _httpClient.GetAsync(
                        CreateEndpointUri("api/tags"),
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

        public Task<string> SendMessageAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("消息内容不能为空。", nameof(message));
            }

            return SendMessagesAsync(
                [new ChatMessage("user", message)],
                cancellationToken);
        }

        public async Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                throw new ArgumentException("System Prompt 不能为空。", nameof(systemPrompt));
            }

            if (string.IsNullOrWhiteSpace(userContext))
            {
                throw new ArgumentException("用户上下文不能为空。", nameof(userContext));
            }

            var modelResponse = await SendMessagesAsync(
                [
                    new ChatMessage("system", systemPrompt),
                    new ChatMessage("user", userContext)
                ],
                cancellationToken);

            return _resultParser.Parse(modelResponse);
        }

        private async Task<string> SendMessagesAsync(
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken)
        {
            try
            {
                return await ExecuteWithTimeoutAsync(async token =>
                {
                    var request = new ChatRequest(
                        _options.ModelName,
                        messages,
                        Stream: false,
                        Think: false,
                        Options: new ChatOptions(Temperature: 0.2));

                    using var response = await _httpClient.PostAsJsonAsync(
                        CreateEndpointUri("api/chat"),
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
                    $"无法连接本地 Ollama 服务：{_baseUri}",
                    exception);
            }
            catch (JsonException exception)
            {
                throw new OllamaServiceException(
                    "Ollama 返回了无法解析的聊天响应。",
                    exception);
            }
        }

        private async Task<T> ExecuteWithTimeoutAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                return await operation(timeoutSource.Token);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Ollama 请求在 {_options.TimeoutSeconds} 秒后超时。",
                    exception);
            }
        }

        private Uri CreateEndpointUri(string relativePath) =>
            new(_baseUri, relativePath);

        private static Uri ValidateAndCreateBaseUri(OllamaOptions options)
        {
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    "Ollama BaseUrl 必须是有效的 HTTP 或 HTTPS 地址。",
                    nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.ModelName))
            {
                throw new ArgumentException("Ollama ModelName 不能为空。", nameof(options));
            }

            if (options.TimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Ollama TimeoutSeconds 必须大于 0。");
            }

            var baseUrl = options.BaseUrl.EndsWith('/')
                ? options.BaseUrl
                : $"{options.BaseUrl}/";
            return new Uri(baseUrl, UriKind.Absolute);
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
            [property: JsonPropertyName("options")] ChatOptions Options);

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
