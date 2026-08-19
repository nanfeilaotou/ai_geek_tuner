using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Safety;

namespace AIGeekTuner.Services.Diagnosis
{
    public sealed class DiagnosisService : IDiagnosisService
    {
        private readonly IAiService _aiService;
        private readonly DiagnosisPromptBuilder _promptBuilder;
        private readonly ISafetyService _safetyService;

        public DiagnosisService(
            IAiService aiService,
            DiagnosisPromptBuilder promptBuilder,
            ISafetyService safetyService)
        {
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
            _safetyService = safetyService ?? throw new ArgumentNullException(nameof(safetyService));
        }

        public async Task<DiagnosisOutcome> DiagnoseAsync(
            DiagnosticRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureOllamaAvailableAsync(cancellationToken);

            string systemPrompt;
            string userContext;
            try
            {
                systemPrompt = _promptBuilder.BuildSystemPrompt();
                userContext = _promptBuilder.BuildUserContext(request);
            }
            catch (Exception exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.PromptGenerationFailed,
                    "生成硬件诊断 Prompt 失败。",
                    exception);
            }

            DiagnosticResult aiResult;
            try
            {
                aiResult = await _aiService.GetDiagnosticResultAsync(
                    systemPrompt,
                    userContext,
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
                    "本地 AI 返回的诊断结果无法解析。",
                    exception);
            }
            catch (OllamaServiceException exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiRequestFailed,
                    "调用本地 Ollama 诊断服务失败。",
                    exception);
            }
            catch (TimeoutException exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiRequestFailed,
                    "本地 AI 诊断请求超时。",
                    exception);
            }
            catch (Exception exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiRequestFailed,
                    "执行本地 AI 诊断时发生错误。",
                    exception);
            }

            if (aiResult is null)
            {
                throw new DiagnosisException(
                    DiagnosisError.AiResponseInvalid,
                    "本地 AI 没有返回诊断结果。");
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
                    "本地 SafetyGuard 检查失败。",
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
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        private async Task EnsureOllamaAvailableAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                if (!await _aiService.IsAvailableAsync(cancellationToken))
                {
                    throw new DiagnosisException(
                        DiagnosisError.OllamaUnavailable,
                        "本地 Ollama 服务不可用，请确认服务已经启动。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DiagnosisException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DiagnosisException(
                    DiagnosisError.OllamaUnavailable,
                    "无法检查本地 Ollama 服务状态。",
                    exception);
            }
        }

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
