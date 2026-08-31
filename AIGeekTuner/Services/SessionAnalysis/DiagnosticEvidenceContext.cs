using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 送给 Session AI 的唯一合法输入（V2-M4.3）：有界的确定性证据组合。
    ///
    /// 证据分层不变量延续 M4.2：
    /// - Telemetry = 现有 TelemetryAnalysisContext（遥测确定性证据，不复制 DTO）；
    /// - WindowsIncidents = incidents.json 的有界 AI 视图（deterministic reducer 产出）；
    ///   incidents.json 本体永远完整保留，reducer 只影响 AI 输入。
    ///
    /// correlation ≠ causation：本上下文只描述“Windows 事件落在会话关联查询窗口内、
    /// 与会话开始的时间偏移”，不携带任何因果字段（无 Cause/Precursor/Score）。
    /// </summary>
    public sealed record DiagnosticEvidenceContext(
        DiagnosticEvidenceMetadata Metadata,
        TelemetrySessionAnalyzer.TelemetryAnalysisContext Telemetry,
        DiagnosticIncidentEvidence? WindowsIncidents);

    public sealed record DiagnosticEvidenceMetadata(
        string SessionId,
        DateTimeOffset GeneratedAtUtc,
        bool IncidentEvidencePresent);

    /// <summary>
    /// incidents.json 的有界 AI 视图。Total/CategoryCounts 保存完整证据信息
    /// （包含被 reducer 省略的部分），Incidents 只含入选条目。
    /// </summary>
    public sealed record DiagnosticIncidentEvidence(
        IncidentQueryStatus QueryStatus,
        IReadOnlyList<IncidentChannelResult> Channels,
        int TotalIncidentCount,
        int IncludedIncidentCount,
        int OmittedIncidentCount,
        IReadOnlyList<IncidentCategoryCount> CategoryCounts,
        IReadOnlyList<IncidentEvidenceItem> Incidents);

    public sealed record IncidentCategoryCount(IncidentCategory Category, int Count);

    /// <summary>时间关系只描述与 Session 边界的相对位置（±30s buffer 区域），不做因果暗示。</summary>
    public enum IncidentTemporalRelation
    {
        BeforeSession,
        WithinSession,
        AfterSession,
    }

    /// <summary>紧凑 incident AI 条目：不含 Details（上限 2000 字符），只含有界 Summary。</summary>
    public sealed record IncidentEvidenceItem(
        string EvidenceId,
        DateTimeOffset OccurredAtUtc,
        long OffsetFromSessionStartMs,
        IncidentTemporalRelation TemporalRelation,
        IncidentCategory Category,
        IncidentSeverity Severity,
        string ProviderName,
        int EventId,
        string Summary);
}
