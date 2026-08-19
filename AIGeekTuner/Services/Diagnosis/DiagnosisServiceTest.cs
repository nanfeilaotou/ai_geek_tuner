using System.IO;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Diagnosis
{
    public static class DiagnosisServiceTest
    {
        public static async Task<DiagnosisOutcome> RunAsync(
            IDiagnosisService diagnosisService,
            DiagnosisPromptBuilder promptBuilder,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(diagnosisService);
            ArgumentNullException.ThrowIfNull(promptBuilder);
            ArgumentNullException.ThrowIfNull(output);

            var request = CreateRequest();
            var userContext = promptBuilder.BuildUserContext(request);
            ValidatePrompt(userContext);

            var outcome = await diagnosisService.DiagnoseAsync(
                request,
                cancellationToken);

            if (!ReferenceEquals(request, outcome.Request))
            {
                throw new InvalidOperationException("DiagnosisOutcome 没有保存原始请求。");
            }

            if (string.IsNullOrWhiteSpace(outcome.AiResult.Summary))
            {
                throw new InvalidOperationException("AI 没有生成有效 DiagnosticResult。");
            }

            if (outcome.Safety.Status == SafetyStatus.Pending)
            {
                throw new InvalidOperationException("SafetyGuard 没有完成安全检查。");
            }

            await output.WriteLineAsync("REQUEST_PRESERVED=True");
            await output.WriteLineAsync("PROMPT_GENERATED=True");
            await output.WriteLineAsync("AI_RESULT_CREATED=True");
            await output.WriteLineAsync("OUTCOME_CREATED=True");
            await output.WriteLineAsync("SAFETY_CHECK_COMPLETED=True");

            return outcome;
        }

        public static DiagnosticRequest CreateRequest()
        {
            return new DiagnosticRequest
            {
                Hardware = new HardwareInfo
                {
                    CpuName = "Intel i9-13980HX",
                    TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
                    MemoryType = "DDR5",
                    MemorySpeedsMHz = [5600],
                    DetectedAt = DateTimeOffset.UtcNow
                },
                SystemContext = new SystemContext
                {
                    OperatingSystemVersion = "Windows 11 Test",
                    CpuName = "Intel i9-13980HX",
                    CpuCoreCount = 24,
                    TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
                    SystemBootTime = DateTimeOffset.UtcNow.AddHours(-2)
                },
                KnowledgeContext =
                [
                    new DiagnosticKnowledgeEntry
                    {
                        Name = "MEMORY_MANAGEMENT",
                        Description = "内存错误辅助知识",
                        Keywords = ["TM5", "MEMORY"],
                        CommonCauses = ["稳定性不足"],
                        VerificationSteps = ["重新测试"],
                        RiskLevel = DiagnosticRiskLevel.High
                    }
                ],
                FaultLog = FaultLog.FromPastedText(
                    "TM5 Error 2 memory instability")
            };
        }

        private static void ValidatePrompt(string userContext)
        {
            var requiredValues = new[]
            {
                "Intel i9-13980HX",
                "DDR5",
                "5600",
                "Windows 11 Test",
                "cpuCoreCount",
                "knowledgeContext",
                "MEMORY_MANAGEMENT",
                "TM5 Error 2 memory instability"
            };

            if (requiredValues.Any(value =>
                    !userContext.Contains(value, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("生成的 Prompt 缺少测试上下文。");
            }
        }
    }
}
