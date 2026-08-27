using AIGeekTuner.Services.Telemetry.Recording;
using System.Text;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Services.SessionAnalysis
{
    public interface ISessionAnalysisPromptBuilder
    {
        string BuildSystemPrompt();

        string BuildUserPrompt(TelemetrySessionAnalyzer.TelemetryAnalysisContext context);

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

