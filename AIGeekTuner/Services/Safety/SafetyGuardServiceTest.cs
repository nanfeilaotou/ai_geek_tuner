using System.IO;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety
{
    public static class SafetyGuardServiceTest
    {
        public static async Task RunAsync(
            ISafetyService safetyService,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(safetyService);
            ArgumentNullException.ThrowIfNull(output);

            var normalResult = CreateResult(
                summary: "检测到内存稳定性错误。",
                rootCause: "日志表明内存子系统可能不稳定。",
                confidence: 0.8,
                recommendation: "重新测试内存稳定性");
            var normalSafety = await safetyService.ValidateAsync(
                normalResult,
                cancellationToken);
            AssertStatus(normalSafety, SafetyStatus.Approved, "正常建议");
            await output.WriteLineAsync("NORMAL_APPROVED=True");

            var dangerousResult = CreateResult(
                summary: "检测到稳定性问题。",
                rootCause: "具体原因尚未确定。",
                confidence: 0.8,
                recommendation: "将Vcore调整到1.8V");
            var dangerousSafety = await safetyService.ValidateAsync(
                dangerousResult,
                cancellationToken);
            AssertStatus(dangerousSafety, SafetyStatus.Rejected, "危险电压建议");
            await output.WriteLineAsync("DANGEROUS_VOLTAGE_REJECTED=True");

            var overcertainResult = CreateResult(
                summary: "你的内存一定损坏，需要立即更换",
                rootCause: "当前日志证据不足。",
                confidence: 0.3,
                recommendation: "重新测试内存稳定性");
            var overcertainSafety = await safetyService.ValidateAsync(
                overcertainResult,
                cancellationToken);
            AssertStatus(
                overcertainSafety,
                SafetyStatus.ApprovedWithWarnings,
                "低置信度确定性结论");
            await output.WriteLineAsync("LOW_CONFIDENCE_WARNING=True");
        }

        private static DiagnosticResult CreateResult(
            string summary,
            string rootCause,
            double confidence,
            string recommendation)
        {
            return new DiagnosticResult
            {
                Summary = summary,
                RootCause = rootCause,
                Confidence = confidence,
                RiskLevel = DiagnosticRiskLevel.Medium,
                Evidence =
                [
                    new DiagnosticEvidence
                    {
                        Kind = EvidenceKind.Fact,
                        Description = "测试日志包含内存稳定性错误。"
                    }
                ],
                Recommendations =
                [
                    new Recommendation
                    {
                        Action = recommendation,
                        Reason = "用于 SafetyGuard 规则测试。",
                        RiskLevel = DiagnosticRiskLevel.Low
                    }
                ]
            };
        }

        private static void AssertStatus(
            SafetyResult result,
            SafetyStatus expected,
            string testName)
        {
            if (result.Status != expected)
            {
                throw new InvalidOperationException(
                    $"{testName}期望 {expected}，实际为 {result.Status}。");
            }
        }
    }
}
