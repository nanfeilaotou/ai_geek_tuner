using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;

namespace AIGeekTuner.Services.Diagnosis
{
    public static class StructuredDiagnosisTest
    {
        public static Task<DiagnosticResult> RunAsync(
            IAiService aiService,
            DiagnosisPromptBuilder promptBuilder,
            CancellationToken cancellationToken = default)
        {
            var request = new DiagnosticRequest
            {
                Hardware = new HardwareInfo
                {
                    TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
                    MemoryType = "DDR5",
                    DetectedAt = DateTimeOffset.UtcNow
                },
                FaultLog = FaultLog.FromPastedText(
                    "TM5 Error 2 memory instability")
            };

            return aiService.GetDiagnosticResultAsync(
                promptBuilder.BuildSystemPrompt(),
                promptBuilder.BuildUserContext(request),
                cancellationToken);
        }
    }
}
