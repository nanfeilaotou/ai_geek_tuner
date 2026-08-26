namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// 来源真实读数：保留 native id / label / 单位与父设备，
    /// 是未来 Recorder / Debug 的证据层；规范化不得破坏或覆盖本层。
    /// <see cref="Device"/> 是源侧本地身份（未经跨源合并）；
    /// <see cref="DeviceInfo"/> 携带该设备的完整源侧事实供 Reconciliation 使用。
    /// </summary>
    public sealed record RawTelemetryReading(
        TelemetrySourceKind Source,
        string SourceMetricId,
        string Label,
        double Value,
        TelemetryUnit Unit,
        TelemetryDeviceIdentity Device,
        SourceDeviceInfo DeviceInfo,
        DateTimeOffset CapturedAtUtc);
}
