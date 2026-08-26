using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.AI
{
    /// <summary>设置页连接检测的三态结果。</summary>
    public enum OllamaReadinessStatus
    {
        ServiceUnavailable,
        ModelMissing,
        Ready
    }

    /// <summary>
    /// 就绪检测结果：面向用户的中文消息与当前已安装模型列表。
    /// 技术性异常细节写入诊断日志，不直接展示给用户。
    /// </summary>
    public sealed record OllamaReadinessResult(
        OllamaReadinessStatus Status,
        string Message,
        IReadOnlyList<string> Models);

    /// <summary>Settings/Ollama 管理专用的轻量连接抽象；IAiService 保持纯诊断职责。</summary>
    public interface IOllamaConnectionService
    {
        Task<OllamaReadinessResult> CheckReadinessAsync(
            string baseUrl,
            string modelName,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> GetModelsAsync(
            string baseUrl,
            CancellationToken cancellationToken = default);
    }

    /// <summary>连接或响应解析失败时抛出；Message 面向用户可直接展示。</summary>
    public sealed class OllamaConnectionException : Exception
    {
        public OllamaConnectionException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    public sealed class OllamaConnectionService : IOllamaConnectionService
    {
        private const int DefaultCheckTimeoutSeconds = 10;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly int _checkTimeoutSeconds;

        public OllamaConnectionService(HttpClient httpClient, int checkTimeoutSeconds = DefaultCheckTimeoutSeconds)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _checkTimeoutSeconds = checkTimeoutSeconds;
        }

        public async Task<OllamaReadinessResult> CheckReadinessAsync(
            string baseUrl,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> models;
            try
            {
                models = await GetModelsAsync(baseUrl, cancellationToken);
            }
            catch (OllamaConnectionException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama connection check");
                return new OllamaReadinessResult(
                    OllamaReadinessStatus.ServiceUnavailable,
                    "Ollama 服务未启动或无法连接。",
                    Array.Empty<string>());
            }

            var installed = models.Any(model =>
                string.Equals(model, modelName, StringComparison.OrdinalIgnoreCase));
            if (!installed)
            {
                return new OllamaReadinessResult(
                    OllamaReadinessStatus.ModelMissing,
                    $"已连接 Ollama，但未找到模型 {modelName}。请先执行 ollama pull 或刷新模型列表。",
                    models);
            }

            return new OllamaReadinessResult(
                OllamaReadinessStatus.Ready,
                $"Ollama 已就绪 · {modelName}",
                models);
        }

        public async Task<IReadOnlyList<string>> GetModelsAsync(
            string baseUrl,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(_checkTimeoutSeconds));

                using var response = await _httpClient.GetAsync(
                    BuildTagsUri(baseUrl),
                    timeoutSource.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new OllamaConnectionException(
                        $"Ollama 返回了错误状态码 {(int)response.StatusCode}。");
                }

                var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(
                    JsonOptions,
                    timeoutSource.Token);
                if (tags?.Models is null)
                {
                    throw new OllamaConnectionException(
                        "Ollama 模型列表响应格式无法解析。");
                }

                return tags.Models
                    .Select(model => model.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray();
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                ExceptionLogWriter.Write(exception, "Ollama connection check");
                throw new OllamaConnectionException(
                    "连接 Ollama 超时，请确认服务正在运行。", exception);
            }
            catch (HttpRequestException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama connection check");
                throw new OllamaConnectionException(
                    "Ollama 服务未启动或无法连接。", exception);
            }
            catch (JsonException exception)
            {
                ExceptionLogWriter.Write(exception, "Ollama connection check");
                throw new OllamaConnectionException(
                    "Ollama 模型列表响应格式无法解析。", exception);
            }
        }

        private Uri BuildTagsUri(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new OllamaConnectionException(
                    $"Ollama 服务地址无效：{baseUrl}");
            }

            var normalized = baseUrl.EndsWith('/')
                ? baseUrl
                : $"{baseUrl}/";
            return new(new Uri(normalized), "api/tags");
        }

        private sealed record TagsResponse(
            [property: JsonPropertyName("models")] IReadOnlyList<TagModel>? Models);

        private sealed record TagModel(
            [property: JsonPropertyName("name")] string Name);
    }
}
