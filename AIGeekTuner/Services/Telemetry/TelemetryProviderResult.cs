using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// 单个 Provider 一次读取的完整结果：状态 + 原始读数 + 该来源自行映射出的 canonical 读数
    /// + 源侧设备清单（供 Reconciliation）+ 可靠时才填的来源版本。
    /// canonical 映射属于“来源知识”，由各 Provider 自己完成，Hub 只做设备合并与优先级选择。
    /// </summary>
    public sealed record TelemetryProviderResult(
        TelemetrySourceStatus Status,
        string Message,
        IReadOnlyList<RawTelemetryReading> RawReadings,
        IReadOnlyList<TelemetryReading> CanonicalReadings,
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<SourceDeviceInfo>? Devices = null,
        string? SourceVersion = null)
    {
        public IReadOnlyList<SourceDeviceInfo> Devices { get; init; } = Devices ?? [];

        public static TelemetryProviderResult Empty(
            TelemetrySourceKind source,
            TelemetrySourceStatus status,
            string message,
            DateTimeOffset capturedAtUtc)
        {
            return new TelemetryProviderResult(
                status,
                message,
                [],
                [],
                capturedAtUtc);
        }
    }
}
