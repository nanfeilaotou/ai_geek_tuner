using AIGeekTuner.Models;
using AIGeekTuner.Services.Safety;

namespace AIGeekTuner.Tests.Services.Safety;

/// <summary>
/// SafetyGuard 规则行为安全网：按当前真实规则（阈值 1.70V 严格大于、短语忽略大小写、
/// 低置信度阈值 0.65）验证放行 / 拦截 / 警告三条路径。
/// </summary>
public class SafetyGuardServiceTests
{
    private readonly SafetyGuardService _service = new();

    [Fact]
    public async Task Validate_NormalRecommendation_IsApproved()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "当前迹象更符合内存稳定性问题，仍需复测确认。",
            rootCause: "已有证据：TM5 报错；不确定因素：缺少复测数据。",
            confidence: 0.8,
            recommendation: "重新运行内存稳定性测试并记录错误编号"));

        Assert.Equal(SafetyStatus.Approved, result.Status);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Validate_VoltageAboveRejectThreshold_IsRejected()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "可能存在供电问题。",
            rootCause: "证据不足，需要测量确认。",
            confidence: 0.6,
            recommendation: "将 Vcore 提升到 1.85V 后重新测试"));

        Assert.Equal(SafetyStatus.Rejected, result.Status);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("明显异常的硬件电压建议"));
    }

    [Fact]
    public async Task Validate_VoltageExactlyAtThreshold_IsNotRejected()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "可能存在供电问题。",
            rootCause: "证据不足，需要测量确认。",
            confidence: 0.6,
            recommendation: "确认当前 Vcore 是否保持在 1.70V 并记录读数"));

        Assert.NotEqual(SafetyStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Validate_NonPositiveVoltageSuggestion_IsRejected()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "可能存在传感器异常。",
            rootCause: "证据不足。",
            confidence: 0.6,
            recommendation: "将 Vcore 设置为 0V 以观察现象"));

        Assert.Equal(SafetyStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Validate_EnglishDangerousPhraseCaseInsensitive_IsRejected()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "过热导致不稳定。",
            rootCause: "日志显示高温记录。",
            confidence: 0.7,
            recommendation: "You can disable thermal protection to finish the test."));

        Assert.Equal(SafetyStatus.Rejected, result.Status);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("禁用或绕过系统保护"));
    }

    [Fact]
    public async Task Validate_ChineseDangerousPhrase_IsRejected()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "疑似过热保护触发。",
            rootCause: "日志显示温度相关报错。",
            confidence: 0.7,
            recommendation: "尝试关闭过热保护后复现问题"));

        Assert.Equal(SafetyStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Validate_LowConfidenceWithAbsoluteClaim_IsApprovedWithWarnings()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "你的内存一定损坏，需要立即更换",
            rootCause: "当前日志证据不足。",
            confidence: 0.3,
            recommendation: "立即购买并更换内存"));

        Assert.Equal(SafetyStatus.ApprovedWithWarnings, result.Status);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("过度确定性", warning);
    }

    [Fact]
    public async Task Validate_HighConfidenceWithAbsoluteClaim_DoesNotWarn()
    {
        var result = await _service.ValidateAsync(CreateResult(
            summary: "你的内存一定损坏，需要立即更换",
            rootCause: "多项交叉证据一致。",
            confidence: 0.9,
            recommendation: "更换内存"));

        // 过度确定性规则只在低置信度时生效；高置信度下不产生警告。
        Assert.Equal(SafetyStatus.Approved, result.Status);
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
                    Description = "日志中包含稳定性测试错误记录。"
                }
            ],
            Recommendations =
            [
                new Recommendation
                {
                    Action = recommendation,
                    Reason = "用于验证候选原因是否成立。",
                    RiskLevel = DiagnosticRiskLevel.Low,
                    Precautions = ["先备份当前配置"]
                }
            ]
        };
    }
}
