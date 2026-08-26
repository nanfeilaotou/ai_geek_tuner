using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>读取器一次快照的结果。</summary>
    public sealed record HwInfoReaderOutcome
    {
        public bool Available { get; private init; }

        public string? UnavailableReason { get; private init; }

        public string? FailureDetail { get; private init; }

        public IReadOnlyList<HwInfoSensorEntry> Sensors { get; private init; } = [];

        public IReadOnlyList<HwInfoReadingEntry> Readings { get; private init; } = [];

        /// <summary>SHM header 报告的接口版本（如 “v3.1”）；仅在成功读取时提供。</summary>
        public string? SourceVersion { get; private init; }

        public static HwInfoReaderOutcome SharedMemoryNotAvailable(string reason) =>
            new() { Available = false, UnavailableReason = reason };

        public static HwInfoReaderOutcome ReadFailed(string failureDetail) =>
            new() { Available = false, FailureDetail = failureDetail };

        public static HwInfoReaderOutcome Snapshot(
            IReadOnlyList<HwInfoSensorEntry> sensors,
            IReadOnlyList<HwInfoReadingEntry> readings,
            string? sourceVersion = null) =>
            new() { Available = true, Sensors = sensors, Readings = readings, SourceVersion = sourceVersion };
    }
}
