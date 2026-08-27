using System.Text.Json.Serialization;

namespace AIGeekTuner.Models.Sessions
{
    public enum SessionOverallAssessment
    {
        Normal,
        Attention,
        PotentialIssue,
        InsufficientData,
    }

    public enum SessionFindingCategory
    {
        Thermal,
        Performance,
        Power,
        Clock,
        Memory,
        Storage,
        Stability,
        DataQuality,
        Other,
    }

    /// <summary>单条发现；EvidenceIds 必须全部存在于分析上下文（反编造核心机制）。</summary>
    public sealed record SessionFinding(
        string Title,
        SessionFindingCategory Category,
        string Assessment,
        string Explanation,
        IReadOnlyList<string> EvidenceIds);

    public sealed record SessionRecommendation(string Text);

    /// <summary>
    /// Session AI 分析结果（M3 独立 schema，与 V1 DiagnosticResult 完全隔离）。
    /// SpokenSummary 与详细结果来自同一次模型调用（§17），仅概括已有结论。
    /// </summary>
    public sealed record SessionAnalysisResult(
        string Summary,
        SessionOverallAssessment OverallAssessment,
        double Confidence,
        IReadOnlyList<SessionFinding> Findings,
        IReadOnlyList<SessionRecommendation> Recommendations,
        IReadOnlyList<string> Uncertainties,
        string SpokenSummary);
}
