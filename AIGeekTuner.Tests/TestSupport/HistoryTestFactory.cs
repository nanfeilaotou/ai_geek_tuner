using AIGeekTuner.Models;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Tests.TestSupport;

/// <summary>
/// History 测试专用夹具：全部路径指向临时目录，绝不触碰用户真实 AppData。
/// </summary>
internal static class HistoryTestFactory
{
    public static ApplicationDataPaths CreatePaths(TempDirectory temp)
    {
        return new ApplicationDataPaths(
            rootDirectory: temp.FullPath,
            legacyRootDirectory: temp.Combine("legacy-root"));
    }

    public static DiagnosisOutcome CreateOutcome(
        string summary = "当前迹象更符合内存稳定性问题",
        DateTimeOffset? completedAt = null,
        string? logContent = null)
    {
        return new DiagnosisOutcome
        {
            Request = new DiagnosticRequest
            {
                Hardware = new HardwareInfo
                {
                    CpuName = "TEST-CPU",
                    TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
                    DetectedAt = DateTimeOffset.UnixEpoch
                },
                FaultLog = FaultLog.FromPastedText(logContent ?? "TM5 Error 2 memory instability")
            },
            AiResult = new DiagnosticResult
            {
                Summary = summary,
                RootCause = "已有证据：TM5 报错；不确定因素：缺少复测数据。无法确定，需要进一步测试",
                Confidence = 0.45,
                RiskLevel = DiagnosticRiskLevel.Medium,
                Evidence =
                [
                    new DiagnosticEvidence
                    {
                        Kind = EvidenceKind.Fact,
                        Description = "日志中记录 TM5 Error 2"
                    }
                ],
                Recommendations =
                [
                    new Recommendation
                    {
                        Action = "重新运行内存稳定性测试",
                        Reason = "用于确认问题是否可复现",
                        RiskLevel = DiagnosticRiskLevel.Low,
                        Precautions = ["保存当前配置"]
                    }
                ]
            },
            Safety = new SafetyResult { Status = SafetyStatus.Approved },
            CompletedAt = completedAt ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };
    }
}
