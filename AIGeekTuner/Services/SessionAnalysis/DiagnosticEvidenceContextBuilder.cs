using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 确定性证据组合层（V2-M4.3）：TelemetryAnalysisContext + incidents.json envelope
    /// → 有界 DiagnosticEvidenceContext。
    ///
    /// Gate B：不污染 TelemetrySessionAnalyzer（它继续只产出遥测上下文）；
    /// Gate D：deterministic reducer 只压缩 AI 输入，incidents.json 永不被修改；
    /// Gate E：入选 incident 保留 incidents.json 原始 EvidenceId，不重排重编号、不重新查询。
    /// </summary>
    public static class DiagnosticEvidenceContextBuilder
    {
        /// <summary>AI 视图最多入选条数（Gate D）。</summary>
        public const int MaxIncludedIncidents = 20;

        /// <summary>WER 单类上限：数量巨大也不许挤占高价值类别名额（Gate D）。</summary>
        public const int WindowsErrorReportingCap = 4;

        /// <summary>类别优先级：越靠前越先入选（确定性，无 scoring）。</summary>
        private static readonly IReadOnlyList<IncidentCategory> CategoryPriority =
        [
            IncidentCategory.HardwareError,
            IncidentCategory.BugCheck,
            IncidentCategory.DisplayDriver,
            IncidentCategory.Storage,
            IncidentCategory.UnexpectedShutdown,
            IncidentCategory.ApplicationCrash,
            IncidentCategory.ApplicationHang,
            IncidentCategory.WindowsErrorReporting,
            IncidentCategory.Other,
        ];

        private static readonly IReadOnlyList<IncidentSeverity> SeverityPriority =
        [
            IncidentSeverity.Critical,
            IncidentSeverity.Error,
            IncidentSeverity.Warning,
            IncidentSeverity.Information,
        ];

        public static DiagnosticEvidenceContext Build(
            TelemetrySessionAnalyzer.TelemetryAnalysisContext telemetry,
            SessionIncidentEnvelope? incidents)
        {
            ArgumentNullException.ThrowIfNull(telemetry);
            return new DiagnosticEvidenceContext(
                new DiagnosticEvidenceMetadata(
                    telemetry.Metadata.SessionId,
                    DateTimeOffset.UtcNow,
                    IncidentEvidencePresent: incidents is not null),
                telemetry,
                incidents is null ? null : BuildIncidentEvidence(telemetry, incidents));
        }

        internal static DiagnosticIncidentEvidence BuildIncidentEvidence(
            TelemetrySessionAnalyzer.TelemetryAnalysisContext telemetry,
            SessionIncidentEnvelope incidents)
        {
            ArgumentNullException.ThrowIfNull(telemetry);
            ArgumentNullException.ThrowIfNull(incidents);

            var sessionStart = telemetry.Metadata.StartedAtUtc;
            var sessionEnd = sessionStart + telemetry.Metadata.Duration;

            // 完整证据信息：类别计数覆盖全部 incident（含被省略的）。
            var categoryCounts = incidents.Incidents
                .GroupBy(incident => incident.Category)
                .Select(group => new IncidentCategoryCount(group.Key, group.Count()))
                .OrderBy(count => CategoryRank(count.Category))
                .ThenBy(count => count.Category)
                .ToArray();

            var ordered = incidents.Incidents
                .OrderBy(incident => CategoryRank(incident.Category))
                .ThenBy(incident => SeverityRank(incident.Severity))
                .ThenByDescending(incident => IsWithinSession(incident.OccurredAtUtc, sessionStart, sessionEnd))
                .ThenBy(incident => incident.OccurredAtUtc)
                .ThenBy(incident => incident.EvidenceId, StringComparer.Ordinal)
                .ToList();

            var included = new List<IncidentEvidenceItem>(Math.Min(MaxIncludedIncidents, ordered.Count));
            var werCount = 0;
            foreach (var incident in ordered)
            {
                if (included.Count >= MaxIncludedIncidents)
                {
                    break;
                }

                if (incident.Category == IncidentCategory.WindowsErrorReporting)
                {
                    if (werCount >= WindowsErrorReportingCap)
                    {
                        continue; // WER 让出名额给后续类别。
                    }

                    werCount++;
                }

                included.Add(ToItem(incident, sessionStart, sessionEnd));
            }

            return new DiagnosticIncidentEvidence(
                QueryStatus: incidents.QueryStatus,
                Channels: incidents.Channels,
                TotalIncidentCount: incidents.Incidents.Count,
                IncludedIncidentCount: included.Count,
                OmittedIncidentCount: incidents.Incidents.Count - included.Count,
                CategoryCounts: categoryCounts,
                Incidents: included);
        }

        private static IncidentEvidenceItem ToItem(
            WindowsIncident incident, DateTimeOffset sessionStart, DateTimeOffset sessionEnd) => new(
            EvidenceId: incident.EvidenceId, // Gate E：原样保留，不重新编号。
            OccurredAtUtc: incident.OccurredAtUtc,
            OffsetFromSessionStartMs: (long)Math.Round(
                (incident.OccurredAtUtc - sessionStart).TotalMilliseconds),
            TemporalRelation: Relation(incident.OccurredAtUtc, sessionStart, sessionEnd),
            Category: incident.Category,
            Severity: incident.Severity,
            ProviderName: incident.ProviderName,
            EventId: incident.EventId,
            Summary: incident.Summary);

        /// <summary>Before/After 只表示落在 ±30s buffer 区域，不是前因后果。</summary>
        private static IncidentTemporalRelation Relation(
            DateTimeOffset at, DateTimeOffset sessionStart, DateTimeOffset sessionEnd) =>
            at < sessionStart ? IncidentTemporalRelation.BeforeSession
                : at > sessionEnd ? IncidentTemporalRelation.AfterSession
                : IncidentTemporalRelation.WithinSession;

        private static bool IsWithinSession(DateTimeOffset at, DateTimeOffset start, DateTimeOffset end) =>
            at >= start && at <= end;

        private static int CategoryRank(IncidentCategory category) => Rank(CategoryPriority, category);

        private static int SeverityRank(IncidentSeverity severity) => Rank(SeverityPriority, severity);

        private static int Rank<T>(IReadOnlyList<T> order, T value)
        {
            for (var i = 0; i < order.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(order[i], value))
                {
                    return i;
                }
            }

            return order.Count;
        }
    }
}
