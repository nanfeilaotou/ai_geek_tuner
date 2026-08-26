using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.ViewModels
{
    /// <summary>数据源状态行的紧凑展示模型（Hardware 页与 Settings 页共用）。</summary>
    public sealed class TelemetrySourceStatusViewModel : ViewModelBase
    {
        public TelemetrySourceStatusViewModel(TelemetrySourceReport report)
        {
            SourceName = SourceDisplayName(report.Source);
            StatusKind = report.Status.ToString();
            StatusGlyph = GlyphOf(report.Status);
            DetailLine = DetailOf(report) + DurationSuffix(report);
            Version = report.SourceVersion;
        }

        public string? Version { get; }

        private static string DurationSuffix(TelemetrySourceReport report) =>
            report.ReadDurationMs is long value and > 0
                ? $" · {value} ms"
                : string.Empty;

        public string SourceName { get; }

        public string StatusKind { get; }

        public string StatusGlyph { get; }

        public string DetailLine { get; }

        internal static string SourceDisplayName(TelemetrySourceKind source) =>
            source switch
            {
                TelemetrySourceKind.HwInfo => "HWiNFO",
                TelemetrySourceKind.Aida64 => "AIDA64",
                _ => "LibreHardwareMonitor"
            };

        private static string GlyphOf(TelemetrySourceStatus status) =>
            status switch
            {
                TelemetrySourceStatus.Ready => "●",
                TelemetrySourceStatus.Unavailable => "○",
                TelemetrySourceStatus.NeedsConfiguration => "△",
                TelemetrySourceStatus.Degraded => "◐",
                _ => "✕"
            };

        private static string DetailOf(TelemetrySourceReport report) =>
            report.Status switch
            {
                TelemetrySourceStatus.Ready => $"已连接 · {report.RawReadingCount} 项",
                TelemetrySourceStatus.Degraded => $"部分可用 · {report.RawReadingCount} 项",
                TelemetrySourceStatus.Unavailable => "未检测到外部数据",
                TelemetrySourceStatus.NeedsConfiguration => report.Message,
                _ => "读取失败"
            };
    }
}
