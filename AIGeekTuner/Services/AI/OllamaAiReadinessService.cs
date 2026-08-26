using AIGeekTuner.Configuration;

namespace AIGeekTuner.Services.AI
{
    /// <summary>诊断前置就绪三态（Provider 中立命名，仅此一层）。</summary>
    public enum AiReadinessStatus
    {
        Unavailable,
        ModelMissing,
        Ready
    }

    /// <summary>面向用户的中文就绪结果。</summary>
    public sealed record AiReadinessResult(AiReadinessStatus Status, string Message);

    /// <summary>
    /// 诊断入口在真正调用模型前使用的前置检查契约。
    /// 与 IAiService 分离：readiness 属于流程编排关注点，不属于推理职责。
    /// </summary>
    public interface IAiReadinessService
    {
        Task<AiReadinessResult> CheckReadinessAsync(
            OllamaOptions options,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Ollama 实现：复用连接服务的 tags 检测与模型比对。</summary>
    public sealed class OllamaAiReadinessService : IAiReadinessService
    {
        private readonly IOllamaConnectionService _connectionService;

        public OllamaAiReadinessService(IOllamaConnectionService connectionService)
        {
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        }

        public async Task<AiReadinessResult> CheckReadinessAsync(
            OllamaOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            var result = await _connectionService.CheckReadinessAsync(
                options.BaseUrl,
                options.ModelName,
                cancellationToken);
            return new AiReadinessResult(ToAiStatus(result.Status), result.Message);
        }

        private static AiReadinessStatus ToAiStatus(OllamaReadinessStatus status) =>
            status switch
            {
                OllamaReadinessStatus.Ready => AiReadinessStatus.Ready,
                OllamaReadinessStatus.ModelMissing => AiReadinessStatus.ModelMissing,
                _ => AiReadinessStatus.Unavailable
            };
    }
}
