namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// 单次快照中一个数据源的状态结果；异常细节只进日志，不上 UI。
    /// SourceVersion 只在能可靠取得时填写（如 HWiNFO SHM header version），
    /// 绝不伪造。
    /// </summary>
    public sealed record TelemetrySourceReport(
        TelemetrySourceKind Source,
        TelemetrySourceStatus Status,
        string Message,
        int RawReadingCount,
        DateTimeOffset? LastReadAtUtc,
        int CanonicalReadingCount = 0,
        long? ReadDurationMs = null,
        string? SourceVersion = null);
}
