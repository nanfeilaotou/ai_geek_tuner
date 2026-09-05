using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Safety;

namespace AIGeekTuner.Services.Diagnosis
{
    /// <summary>
    /// AI 智能诊断编排（V2-M5.1B 起 provider-aware）。
    /// 只替换了“发送层”：Prompt 构建、结果解析、唯一一次 repair、SafetyGuard、
    /// 失败分类（<see cref="DiagnosisFailurePolicy"/>）全部保持原语义。
    /// Gate D：一次诊断全程只使用开始时捕获的一份不可变 <see cref="AiRuntimeSnapshot"/>；
    /// Gate I：repair 与第一次请求使用同一份快照；HTTP/超时/取消/认证/模型拒绝不做 repair。
    /// </summary>
    public sealed class DiagnosisService : IDiagnosisService
    {
        private readonly IAiChatRuntime _aiRuntime;
        private readonly DiagnosisPromptBuilder _promptBuilder;
        private readonly ISafetyService _safetyService;
        private readonly DiagnosticResultParser _resultParser;

        public DiagnosisService(
            IAiChatRuntime aiRuntime,
            DiagnosisPromptBuilder promptBuilder,
            ISafetyService safetyService,
            DiagnosticResultParser? resultParser = null)
        {
            _aiRuntime = aiRuntime ?? throw new ArgumentNullException(nameof(aiRuntime));
            _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
            _safetyService = safetyService ?? throw new ArgumentNullException(nameof(safetyService));
            _resultParser = resultParser ?? new DiagnosticResultParser();
        }

        public async Task<DiagnosisOutcome> DiagnoseAsync(
            DiagnosticRequest request,
            DiagnosticConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            // Gate D/O：快照超时取自本次配置快照（全局请求超时），快照之后设置变化不影响本次。
            var runtime = await CaptureRuntimeAsync(
                configuration.Ollama.TimeoutSeconds,
                cancellationToken);

            // Gate J：只有 Ollama Native 保留廉价 /api/tags 就绪检查；
            // OpenAI 兼容 Provider 不做 /models 预检，直接发送真实请求。
            await EnsureReadyAsync(runtime, cancellationToken);

            string systemPrompt;
            string userContext;
            try
            {
                systemPrompt = _promptBuilder.BuildSystemPrompt();
                userContext = _promptBuilder.BuildUserContext(
                    request,
                    configuration.Input);
            }
            catch (Exception exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.PromptGenerationFailed,
                    "生成硬件诊断 Prompt 失败。",
                    exception);
            }

            var messages = new[]
            {
                new AiChatMessage("system", systemPrompt),
                new AiChatMessage("user", userContext)
            };

            DiagnosticResult aiResult;
            try
            {
                aiResult = await RunChatWithSingleRepairAsync(
                    runtime,
                    messages,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DiagnosticResultParsingException exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiResponseInvalid,
                    "AI 返回的诊断结果无法解析。",
                    exception);
            }
            catch (AiRuntimeException exception)
            {
                throw MapAiRuntimeException(exception, runtime);
            }

            if (aiResult is null)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiResponseInvalid,
                    "AI 没有返回诊断结果。");
            }

            SafetyResult safetyResult;
            try
            {
                safetyResult = await _safetyService.ValidateAsync(
                    aiResult,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.SafetyCheckFailed,
                    "SafetyGuard 检查失败。",
                    exception);
            }

            if (safetyResult is null || safetyResult.Status == SafetyStatus.Pending)
            {
                throw new DiagnosisException(
                    DiagnosisError.SafetyCheckFailed,
                    "SafetyGuard 没有返回最终安全判定。");
            }

            return new DiagnosisOutcome
            {
                Request = request,
                AiResult = aiResult,
                Safety = safetyResult,
                CompletedAt = DateTimeOffset.UtcNow,
                ModelName = runtime.ModelId,
                ProviderId = runtime.ProviderId,
                ProviderDisplayName = runtime.ProviderDisplayName
            };
        }

        private async Task<AiRuntimeSnapshot> CaptureRuntimeAsync(
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _aiRuntime.CaptureSnapshotAsync(
                    timeoutSeconds,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AiRuntimeException exception) when (exception.Error == AiRuntimeError.NotConfigured)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiUnavailable,
                    exception.UserMessage,
                    exception);
            }
        }

        private async Task EnsureReadyAsync(
            AiRuntimeSnapshot runtime,
            CancellationToken cancellationToken)
        {
            AiReadinessResult readiness;
            try
            {
                readiness = await _aiRuntime.CheckReadinessAsync(
                    runtime,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AiRuntimeException exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiUnavailable,
                    exception.UserMessage,
                    exception);
            }

            if (readiness.Status != AiReadinessStatus.Ready)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiUnavailable,
                    readiness.Message);
            }
        }

        /// <summary>
        /// 一次 chat + 解析失败后的唯一一次 repair（Gate I）。
        /// 两次请求使用同一份 <paramref name="runtime"/> 快照；传输层异常直接向上传播，不做 repair。
        /// </summary>
        private async Task<DiagnosticResult> RunChatWithSingleRepairAsync(
            AiRuntimeSnapshot runtime,
            IReadOnlyList<AiChatMessage> messages,
            CancellationToken cancellationToken)
        {
            // Gate H：Diagnosis 现契约 = JSON mode + parser 校验（无完整 schema），
            // 等价映射为 JsonObject 意图；协议层具体行为由 transport 按 Profile 模式决定。
            // 采样选项与旧 OllamaService 完全一致：think:false、temperature 0.2。
            var structuredOutput = AiStructuredOutputRequest.JsonObject;
            var chatOptions = new AiChatRuntimeOptions(Think: false, Temperature: 0.2);

            var firstResponse = await _aiRuntime.SendChatAsync(
                runtime,
                messages,
                structuredOutput,
                chatOptions,
                cancellationToken);

            try
            {
                return _resultParser.Parse(firstResponse);
            }
            catch (DiagnosticResultParsingException firstError)
            {
                // 只有“模型成功返回文本但结构无效”才做唯一一次修复重试；
                // HTTP 错误、超时、取消等异常不会进入本分支，直接向上传播。
                var repairMessages = new List<AiChatMessage>(messages)
                {
                    new("assistant", firstResponse),
                    new("user", DiagnosisRepairInstruction.Build(firstError))
                };
                var repairResponse = await _aiRuntime.SendChatAsync(
                    runtime,
                    repairMessages,
                    structuredOutput,
                    chatOptions,
                    cancellationToken);

                try
                {
                    return _resultParser.Parse(repairResponse);
                }
                catch (DiagnosticResultParsingException secondError)
                {
                    throw new DiagnosticResultParsingException(
                        "模型两次输出均无法解析为诊断结果。首次错误：" + firstError.Message
                        + "；修复后错误：" + secondError.Message,
                        secondError);
                }
            }
        }

        /// <summary>运行时错误 → 诊断异常：用户文案来自脱敏的 <see cref="AiRuntimeException.UserMessage"/>。</summary>
        private static DiagnosisException MapAiRuntimeException(
            AiRuntimeException exception,
            AiRuntimeSnapshot runtime) =>
            new(
                DiagnosisError.AiRequestFailed,
                exception.UserMessage,
                exception)
            {
                ModelName = runtime.ModelId,
                ProviderName = runtime.ProviderDisplayName
            };

        private static void ValidateRequest(DiagnosticRequest? request)
        {
            if (request is null)
            {
                throw new DiagnosisException(
                    DiagnosisError.InvalidRequest,
                    "诊断请求不能为空。");
            }

            if (request.FaultLog is null ||
                string.IsNullOrWhiteSpace(request.FaultLog.Content))
            {
                throw new DiagnosisException(
                    DiagnosisError.EmptyFaultLog,
                    "故障日志不能为空。");
            }

            if (request.FaultLog.FileSizeBytes is < 0)
            {
                throw new DiagnosisException(
                    DiagnosisError.InvalidRequest,
                    "故障日志文件大小不能为负数。");
            }

            if (request.Hardware is null || !HasUsableHardwareContext(request.Hardware))
            {
                throw new DiagnosisException(
                    DiagnosisError.HardwareUnavailable,
                    "没有可用于诊断的硬件信息。");
            }
        }

        private static bool HasUsableHardwareContext(HardwareInfo hardware)
        {
            return IsKnown(hardware.CpuName)
                || hardware.GpuNames?.Any(IsKnown) == true
                || hardware.TotalMemoryBytes is > 0
                || hardware.MemoryManufacturers?.Any(IsKnown) == true
                || hardware.MemorySpeedsMHz?.Count > 0
                || IsKnown(hardware.MemoryType)
                || IsKnown(hardware.MotherboardManufacturer)
                || IsKnown(hardware.MotherboardProduct)
                || IsKnown(hardware.OperatingSystemName);
        }

        private static bool IsKnown(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !string.Equals(
                value,
                HardwareInfo.UnknownValue,
                StringComparison.OrdinalIgnoreCase);
    }
}
