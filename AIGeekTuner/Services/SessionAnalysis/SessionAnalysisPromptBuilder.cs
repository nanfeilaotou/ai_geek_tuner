using AIGeekTuner.Services.Telemetry.Recording;
using System.Text;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.SessionAnalysis
{
    public interface ISessionAnalysisPromptBuilder
    {
        string BuildSystemPrompt();

        string BuildUserPrompt(TelemetrySessionAnalyzer.TelemetryAnalysisContext context);

        /// <summary>V2-M4.3：组合证据上下文（telemetry + 可选 Windows incidents）的 user prompt。</summary>
        string BuildUserPrompt(DiagnosticEvidenceContext context);

        string BuildRepairPrompt(IReadOnlyList<string> errors);
    }

    /// <summary>Session 分析 Prompt（§21）：确定性数据 + 反编造 + 证据引用约束。</summary>
    public sealed class SessionAnalysisPromptBuilder : ISessionAnalysisPromptBuilder
    {
        public const int SpokenSummaryMaxLength = 180;

        public string BuildSystemPrompt()
        {
            var builder = new StringBuilder();
            builder.AppendLine("你是 AIGeekTuner 的硬件遥测分析助手。输入内容全部是程序已确定性计算出的统计数据与事件，不是原始日志。");
            builder.AppendLine("严格遵守：");
            builder.AppendLine("1. 禁止编造输入中不存在的温度、功耗、频率、事件或传感器。");
            builder.AppendLine("2. 统计值只是观察结果，不等于硬件故障。");
            builder.AppendLine("3. SignificantChange 只是变化检测阈值触发，不代表异常或损坏。");
            builder.AppendLine("4. 数据来源切换（如 HWiNFO→AIDA64）是正常回退，不是硬件故障。");
            builder.AppendLine("5. 覆盖率不足时必须降低结论强度并在 uncertainties 中说明。");
            builder.AppendLine("6. 不知道厂商具体温度规格时，不得声称超过官方安全上限；可以说值得对照厂商规格进一步确认。");
            builder.AppendLine("7. 不要把瞬时峰值自动等同于过热，不要把频率下降自动等同于故障。");
            builder.AppendLine("8. findings 中每一条都必须在 evidenceIds 中引用真实存在的证据 ID；没有充分证据就不要写该条。");
            builder.AppendLine("9. 如果没有值得关注的发现，findings 可以为空数组，overallAssessment 使用 Normal 或 InsufficientData。");
            builder.AppendLine("10. spokenSummary 必须是完全来自 summary/findings 的简体中文口语总结，1~2 句、40~100 个汉字、纯文本无 Markdown、不得引入新事实。");
            builder.AppendLine("Windows 事件证据（incident:XXXX）规则：");
            builder.AppendLine("11. Windows incidents 是 Windows 事件日志记录的确定性事件证据；与遥测指标时间接近只能说明 temporal correlation，correlation does not establish causation。");
            builder.AppendLine("12. 不得仅因为温度/频率等遥测变化与 crash 事件同时出现，就断言遥测变化导致了 crash。");
            builder.AppendLine("13. Kernel-Power 41 / UnexpectedShutdown 只说明发生了非正常关机，不能单独推断 PSU、CPU、GPU 或其他电源硬件根因。");
            builder.AppendLine("14. WHEA / HardwareError 只说明 Windows 记录到了硬件错误，不得超出事件内容推断具体损坏部件。");
            builder.AppendLine("15. DisplayDriver / TDR 事件只能说明驱动停止响应或恢复，不能单凭事件断言 GPU 硬件损坏。");
            builder.AppendLine("16. ApplicationCrash / ApplicationHang 只说明应用发生了崩溃或无响应；WindowsErrorReporting 是辅助证据，不因数量多而提升结论强度。");
            builder.AppendLine("17. Windows incidents 查询状态为 Partial / PermissionDenied / Unavailable 时，必须在 uncertainties 中说明事件证据覆盖受限。");
            builder.AppendLine("18. 引用 Windows 事件必须使用其 incident:XXXX 证据 ID；上下文未提供 Windows incidents 时，绝不得编造任何 Windows 事件。");
            return builder.ToString();
        }

        public string BuildUserPrompt(TelemetrySessionAnalyzer.TelemetryAnalysisContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("请基于以下确定性会话分析上下文输出 JSON 结果（字段与证据 ID 只能取自下文）。");
            builder.AppendLine();
            builder.AppendLine("[METADATA]");
            builder.AppendLine($"sessionId={context.Metadata.SessionId} duration={context.Metadata.Duration} samples={context.Metadata.SampleCount} intervalMs={context.Metadata.IntervalMs}");
            builder.AppendLine();
            builder.AppendLine("[SOURCES]");
            foreach (var source in context.Sources)
            {
                builder.AppendLine($"{source.Source}: {source.Status} (rawReadings={source.RawReadings})");
            }

            builder.AppendLine();
            builder.AppendLine("[STATISTICS]");
            foreach (var statistic in context.Statistics)
            {
                builder.AppendLine(
                    $"{statistic.EvidenceId} | device={statistic.DeviceKey} metric={statistic.MetricKey} unit={statistic.Unit} "
                    + $"samples={statistic.Samples} coverage={statistic.CoveragePercent}% "
                    + $"min={statistic.Min} avg={statistic.Avg} max={statistic.Max} p95={statistic.P95}");
            }

            builder.AppendLine();
            builder.AppendLine("[TOP EVENTS]");
            foreach (var @event in context.Events)
            {
                builder.AppendLine(
                    $"{@event.EvidenceId} | {@event.Type} | at={@event.TimestampUtc:HH:mm:ss} | source={@event.Source} "
                    + $"metric={@event.MetricKey} | {@event.From} -> {@event.To} | {@event.Detail}");
            }

            builder.AppendLine();
            builder.AppendLine("要求：summary 为 100~400 字的总体说明；confidence 是对本次分析结论的置信度(0.0~1.0)，不是硬件健康度；"
                + "recommendations 仅允许观察/复测/检查驱动/检查散热/对照厂商规格/针对性测试；"
                + "uncertainties 必须明确说明当前数据无法判断的内容。");
            return builder.ToString();
        }

        /// <summary>V2-M4.3：组合证据 user prompt。WindowsIncidents 为 null 时明确告知无事件证据（防编造）。</summary>
        public string BuildUserPrompt(DiagnosticEvidenceContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("请基于以下确定性会话分析上下文输出 JSON 结果（字段与证据 ID 只能取自下文）。");
            builder.AppendLine();
            builder.AppendLine("[METADATA]");
            builder.AppendLine($"sessionId={context.Metadata.SessionId} duration={context.Telemetry.Metadata.Duration} samples={context.Telemetry.Metadata.SampleCount} intervalMs={context.Telemetry.Metadata.IntervalMs}");
            builder.AppendLine();
            builder.AppendLine("[SOURCES]");
            foreach (var source in context.Telemetry.Sources)
            {
                builder.AppendLine($"{source.Source}: {source.Status} (rawReadings={source.RawReadings})");
            }

            builder.AppendLine();
            builder.AppendLine("[STATISTICS]");
            foreach (var statistic in context.Telemetry.Statistics)
            {
                builder.AppendLine(
                    $"{statistic.EvidenceId} | device={statistic.DeviceKey} metric={statistic.MetricKey} unit={statistic.Unit} "
                    + $"samples={statistic.Samples} coverage={statistic.CoveragePercent}% "
                    + $"min={statistic.Min} avg={statistic.Avg} max={statistic.Max} p95={statistic.P95}");
            }

            builder.AppendLine();
            builder.AppendLine("[TOP EVENTS]");
            foreach (var @event in context.Telemetry.Events)
            {
                builder.AppendLine(
                    $"{@event.EvidenceId} | {@event.Type} | at={@event.TimestampUtc:HH:mm:ss} | source={@event.Source} "
                    + $"metric={@event.MetricKey} | {@event.From} -> {@event.To} | {@event.Detail}");
            }

            builder.AppendLine();
            AppendIncidentSection(context, builder);

            builder.AppendLine();
            builder.AppendLine("要求：summary 为 100~400 字的总体说明；confidence 是对本次分析结论的置信度(0.0~1.0)，不是硬件健康度；"
                + "recommendations 仅允许观察/复测/检查驱动/检查散热/对照厂商规格/针对性测试；"
                + "uncertainties 必须明确说明当前数据无法判断的内容。");
            return builder.ToString();
        }

        private static void AppendIncidentSection(DiagnosticEvidenceContext context, StringBuilder builder)
        {
            builder.AppendLine("[WINDOWS INCIDENT EVIDENCE]");
            var incidents = context.WindowsIncidents;
            if (incidents is null)
            {
                builder.AppendLine("none（本会话未采集 Windows 事件证据，不得编造任何 Windows 事件。）");
                return;
            }

            var channels = incidents.Channels.Count == 0
                ? "channels: -"
                : "channels: " + string.Join(", ", incidents.Channels.Select(
                    channel => $"{channel.Channel}={channel.Status}({channel.IncidentCount})"));
            builder.AppendLine(
                $"coverage={incidents.QueryStatus} | {channels} "
                + $"| total={incidents.TotalIncidentCount} included={incidents.IncludedIncidentCount} omitted={incidents.OmittedIncidentCount}");
            builder.AppendLine("categoryCounts: " + string.Join(", ", incidents.CategoryCounts.Select(
                count => $"{count.Category}={count.Count}")));
            foreach (var incident in incidents.Incidents)
            {
                builder.AppendLine(
                    $"{incident.EvidenceId} | {incident.Category} | {incident.Severity} | {incident.ProviderName} {incident.EventId} "
                    + $"| at={incident.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} | offsetFromSessionStart={incident.OffsetFromSessionStartMs}ms "
                    + $"| relation={incident.TemporalRelation} | {incident.Summary}");
            }
        }

        public string BuildRepairPrompt(IReadOnlyList<string> errors)
        {
            var builder = new StringBuilder();
            builder.AppendLine("你上一次输出的 JSON 存在以下问题，请只修复结构与证据引用后重新输出完整 JSON：");
            foreach (var error in errors)
            {
                builder.AppendLine("- " + error);
            }

            builder.AppendLine("不要新增任何事实、数值或证据 ID。");
            return builder.ToString();
        }
    }
}


