using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers.Runtime
{
    /// <summary>
    /// 生产 AI 链路（Diagnosis / SessionAnalysis）使用的小型运行时入口：
    /// 快照捕获（Gate D）→ 就绪检查（Gate J：只有 Ollama Native 做廉价预检）
    /// → provider-aware chat。业务语义（Prompt/Parser/repair/Safety）全部留在调用方。
    /// </summary>
    public interface IAiChatRuntime
    {
        /// <summary>
        /// 捕获一次不可变运行时快照；无可用 Provider 时抛
        /// <see cref="AiRuntimeException"/>（<see cref="AiRuntimeError.NotConfigured"/>）。
        /// </summary>
        Task<AiRuntimeSnapshot> CaptureSnapshotAsync(
            int timeoutSeconds,
            CancellationToken cancellationToken = default);

        /// <summary>请求前就绪检查。OpenAI 兼容 Provider 不做 /models 预检，直接 Ready。</summary>
        Task<AiReadinessResult> CheckReadinessAsync(
            AiRuntimeSnapshot runtime,
            CancellationToken cancellationToken = default);

        /// <summary>发送一次 chat 请求；返回响应文本。错误统一映射为脱敏 AiRuntimeException。</summary>
        Task<string> SendChatAsync(
            AiRuntimeSnapshot runtime,
            IReadOnlyList<AiChatMessage> messages,
            AiStructuredOutputRequest structuredOutput,
            AiChatRuntimeOptions options,
            CancellationToken cancellationToken = default);
    }

    public sealed class AiChatRuntime : IAiChatRuntime
    {
        private readonly IAiRuntimeSnapshotSource _snapshotSource;
        private readonly IAiChatTransport _dispatcher;
        private readonly Services.AI.IOllamaConnectionService? _ollamaConnectionService;

        public AiChatRuntime(
            IAiRuntimeSnapshotSource snapshotSource,
            IAiChatTransport dispatcher,
            Services.AI.IOllamaConnectionService? ollamaConnectionService = null)
        {
            _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _ollamaConnectionService = ollamaConnectionService;
        }

        public async Task<AiRuntimeSnapshot> CaptureSnapshotAsync(
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            var snapshot = await _snapshotSource.TryCaptureAsync(timeoutSeconds, cancellationToken);
            if (snapshot is null)
            {
                throw new AiRuntimeException(
                    AiRuntimeError.NotConfigured,
                    "尚未配置可用的 AI 服务提供方，请前往设置。");
            }

            return snapshot;
        }

        public async Task<AiReadinessResult> CheckReadinessAsync(
            AiRuntimeSnapshot runtime,
            CancellationToken cancellationToken = default)
        {
            if (runtime.ProviderKind != AiProviderKind.OllamaNative)
            {
                // OpenAI 兼容 Provider：不强制 /models（该端点可能不存在），
                // 也不做额外 ping——直接发送真实请求，错误由 transport 统一分类（Gate J）。
                return new AiReadinessResult(
                    AiReadinessStatus.Ready,
                    "已选择 OpenAI 兼容 Provider，直接发送真实请求（无预检）。");
            }

            if (_ollamaConnectionService is null)
            {
                throw new InvalidOperationException(
                    "Ollama Native readiness 检查需要 OllamaConnectionService。");
            }

            try
            {
                var result = await _ollamaConnectionService.CheckReadinessAsync(
                    runtime.BaseUrl,
                    runtime.ModelId,
                    cancellationToken);
                var status = result.Status switch
                {
                    OllamaReadinessStatus.Ready => AiReadinessStatus.Ready,
                    OllamaReadinessStatus.ModelMissing => AiReadinessStatus.ModelMissing,
                    _ => AiReadinessStatus.Unavailable
                };
                return new AiReadinessResult(status, result.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AiRuntimeException(
                    AiRuntimeError.ConnectionUnavailable,
                    "无法检查当前 AI 服务提供方状态。",
                    exception);
            }
        }

        public async Task<string> SendChatAsync(
            AiRuntimeSnapshot runtime,
            IReadOnlyList<AiChatMessage> messages,
            AiStructuredOutputRequest structuredOutput,
            AiChatRuntimeOptions options,
            CancellationToken cancellationToken = default)
        {
            var response = await _dispatcher.SendAsync(
                new AiChatRequest(runtime, messages, structuredOutput, options),
                cancellationToken);
            return response.Content;
        }
    }
}
