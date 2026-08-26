using System.Globalization;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Diagnosis;

namespace AIGeekTuner.Tests.Services.Diagnosis;

/// <summary>
/// Parser 当前行为的基线安全网：
/// 成功用例验证实际字段映射值，失败用例验证异常类型与关键校验语义。
/// </summary>
public class DiagnosticResultParserTests
{
    private readonly DiagnosticResultParser _parser = new();

    private const string ValidJson = """
{
  "summary": "当前迹象更符合内存稳定性问题，仍需复测确认",
  "rootCause": "已有证据：TM5 报错；可能原因：内存稳定性不足；不确定因素：缺少复测数据。无法确定，需要进一步测试",
  "confidence": 0.55,
  "riskLevel": "Medium",
  "evidence": [
    { "kind": "Fact", "description": "日志中记录 TM5 Error 2" },
    { "kind": "Inference", "description": "可能存在内存稳定性不足" }
  ],
  "recommendations": [
    {
      "action": "重新运行内存稳定性测试并记录错误",
      "reason": "用于确认问题是否可复现",
      "riskLevel": "Low",
      "precautions": ["保存当前配置", "避免中途断电"]
    }
  ]
}
""";

    [Fact]
    public void Parse_ValidJson_MapsAllFields()
    {
        var result = _parser.Parse(ValidJson);

        Assert.Equal("当前迹象更符合内存稳定性问题，仍需复测确认", result.Summary);
        Assert.Contains("无法确定，需要进一步测试", result.RootCause);
        Assert.Equal(0.55, result.Confidence, precision: 9);
        Assert.Equal(DiagnosticRiskLevel.Medium, result.RiskLevel);
        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(EvidenceKind.Fact, result.Evidence[0].Kind);
        Assert.Equal("日志中记录 TM5 Error 2", result.Evidence[0].Description);
        Assert.Equal(EvidenceKind.Inference, result.Evidence[1].Kind);
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal("重新运行内存稳定性测试并记录错误", recommendation.Action);
        Assert.Equal("用于确认问题是否可复现", recommendation.Reason);
        Assert.Equal(DiagnosticRiskLevel.Low, recommendation.RiskLevel);
        Assert.Equal(2, recommendation.Precautions.Count);
        Assert.Equal("避免中途断电", recommendation.Precautions[1]);
    }

    [Fact]
    public void Parse_JsonWrappedInMarkdownCodeFence_Succeeds()
    {
        var response = "```json\n" + ValidJson + "\n```";

        var result = _parser.Parse(response);

        Assert.Equal(DiagnosticRiskLevel.Medium, result.RiskLevel);
        Assert.Single(result.Recommendations);
    }

    [Fact]
    public void Parse_ThinkBlockBeforeJson_IsStrippedAndSucceeds()
    {
        var response = "<think>用户提到了 TM5，先考虑内存。</think>\n" + ValidJson;

        var result = _parser.Parse(response);

        Assert.Contains("TM5", result.Evidence[0].Description);
        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public void Parse_NaturalLanguageBeforeJson_Succeeds()
    {
        var response = "好的，以下是我的诊断结论：\n" + ValidJson;

        var result = _parser.Parse(response);

        Assert.StartsWith("当前迹象更符合", result.Summary);
    }

    [Fact]
    public void Parse_NaturalLanguageAfterJson_Succeeds()
    {
        var response = ValidJson + "\n以上分析仅供参考，请结合实际复测结果判断。";

        var result = _parser.Parse(response);

        Assert.Single(result.Recommendations);
    }

    [Fact]
    public void Parse_NoiseAroundJson_Succeeds()
    {
        var response = "根据你提供的日志，我的判断如下：\n```\n"
            + ValidJson
            + "\n```\n希望对你有帮助，如需进一步排查请补充温度信息。";

        var result = _parser.Parse(response);

        Assert.Equal(0.55, result.Confidence, precision: 9);
        Assert.Equal(EvidenceKind.Fact, result.Evidence[0].Kind);
    }

    [Fact]
    public void Parse_TruncatedJson_ThrowsParsingException()
    {
        var truncated = """
{
  "summary": "当前迹象更符合内存稳定性问题",
  "rootCause": "已有证据：TM5 报错；
""";

        var exception = Assert.Throws<DiagnosticResultParsingException>(
            () => _parser.Parse(truncated));

        Assert.Contains("未找到有效的 JSON 对象", exception.Message);
    }

    [Fact]
    public void Parse_MissingRequiredField_ThrowsParsingException()
    {
        var missingRootCause = """
{
  "summary": "摘要",
  "confidence": 0.5,
  "riskLevel": "Low",
  "evidence": [],
  "recommendations": []
}
""";

        var exception = Assert.Throws<DiagnosticResultParsingException>(
            () => _parser.Parse(missingRootCause));

        Assert.Contains("缺少必需字段", exception.Message);
    }

    [Fact]
    public void Parse_UnknownEnumText_ThrowsParsingException()
    {
        var invalidEnum = ValidJson.Replace("\"riskLevel\": \"Medium\"", "\"riskLevel\": \"Severe\"");

        Assert.Throws<DiagnosticResultParsingException>(() => _parser.Parse(invalidEnum));
    }

    [Fact]
    public void Parse_IntegerEnumValue_IsRejected()
    {
        var integerEnum = ValidJson.Replace("\"riskLevel\": \"Medium\"", "\"riskLevel\": 2");

        Assert.Throws<DiagnosticResultParsingException>(() => _parser.Parse(integerEnum));
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void Parse_ConfidenceOutOfRange_ThrowsParsingException(double confidence)
    {
        var outOfRange = ValidJson.Replace(
            "\"confidence\": 0.55",
            FormattableString.Invariant($"\"confidence\": {confidence.ToString(CultureInfo.InvariantCulture)}"));

        var exception = Assert.Throws<DiagnosticResultParsingException>(
            () => _parser.Parse(outOfRange));

        Assert.Contains("Confidence 必须在 0 到 1 之间", exception.Message);
    }

    [Fact]
    public void Parse_PlainNaturalLanguageWithoutJson_ThrowsParsingException()
    {
        var exception = Assert.Throws<DiagnosticResultParsingException>(
            () => _parser.Parse("抱歉，我无法基于当前信息给出结构化诊断，建议提供更多日志。"));

        Assert.Contains("未找到有效的 JSON 对象", exception.Message);
    }

    [Fact]
    public void Parse_EmptyObject_ThrowsParsingException()
    {
        var exception = Assert.Throws<DiagnosticResultParsingException>(() => _parser.Parse("{}"));

        // {} 缺失全部 required 成员，走 JsonException 包装路径而非“找不到 JSON”路径。
        Assert.Contains("缺少必需字段", exception.Message);
    }
}
