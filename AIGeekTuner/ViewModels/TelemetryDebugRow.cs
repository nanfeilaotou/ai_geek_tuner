using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.ViewModels
{
    /// <summary>数据源详情对话框的一行：Raw 事实 + 其 canonical 归属（§25 轻量调试入口）。</summary>
    public sealed record TelemetryDebugRow(
        string Source,
        string CanonicalDevice,
        string NativeDeviceId,
        string NativeId,
        string NativeLabel,
        string ValueText,
        string CanonicalMetric);
}
