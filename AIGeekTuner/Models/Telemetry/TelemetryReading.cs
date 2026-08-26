namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// 规范化读数：统一单位 + 设备维度 + 来源溯源。
    /// Source / SourceMetricId / CapturedAtUtc 是 Evidence 与 Source switching 的基础，禁止丢弃。
    /// 本轮不引入任何“精度/置信度”伪科学字段。
    /// </summary>
    public sealed record TelemetryReading(
        TelemetryMetricKey MetricKey,
        double Value,
        TelemetryUnit Unit,
        TelemetryDeviceIdentity Device,
        TelemetrySourceKind Source,
        string SourceMetricId,
        string? SourceLabel,
        DateTimeOffset CapturedAtUtc);
}
