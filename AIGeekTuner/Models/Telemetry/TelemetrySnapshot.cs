namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// 一次统一的遥测快照：canonical 读数 + 各来源状态 + 原始读数（调试/详情用）。
    /// </summary>
    public sealed record TelemetrySnapshot(
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<TelemetryReading> CanonicalReadings,
        IReadOnlyList<TelemetrySourceReport> Sources,
        IReadOnlyList<RawTelemetryReading> RawReadings)
    {
        public static TelemetrySnapshot Empty(DateTimeOffset capturedAtUtc) =>
            new(capturedAtUtc, [], [], []);
    }
}
