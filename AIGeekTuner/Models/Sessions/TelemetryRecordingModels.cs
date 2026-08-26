using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Models.Sessions
{
    public enum RecordingStatus
    {
        Recording = 0,
        Completed = 1,
        Cancelled = 2,
        Failed = 3
    }

    public enum TelemetrySessionEventType
    {
        SourceStateChanged,
        SourceUnavailable,
        FallbackSourceChanged,
        MetricSourceChanged,
        SignificantChange,
        ThrottleObserved,
        SampleGap
    }

    /// <summary>一次 Hub 采样：只含 canonical 读数与真实时间戳（§5）。</summary>
    public sealed record TelemetrySample(
        int Sequence,
        DateTimeOffset CapturedAtUtc,
        long ReadDurationMs,
        IReadOnlyList<TelemetryReading> Readings);

    /// <summary>会话级事件；只记录“变化”，不在每个采样重复。</summary>
    public sealed record TelemetrySessionEvent(
        TelemetrySessionEventType Type,
        DateTimeOffset TimestampUtc,
        string? Source,
        string? MetricKey,
        string? DeviceKey,
        string From,
        string To,
        string Detail)
    {
        public static TelemetrySessionEvent Simple(
            TelemetrySessionEventType type,
            DateTimeOffset at,
            string detail) =>
            new(type, at, null, null, null, string.Empty, string.Empty, detail);
    }

    /// <summary>单条设备×指标序列的统计。百分位采用最近秩（nearest-rank）法。</summary>
    public sealed record MetricSeriesStatistic(
        string DeviceKey,
        string DeviceName,
        string MetricKey,
        string Unit,
        int SampleCount,
        double CoveragePercent,
        double Minimum,
        double Maximum,
        double Average,
        double P50,
        double P95,
        double P99,
        DateTimeOffset FirstAtUtc,
        DateTimeOffset LastAtUtc);

    /// <summary>事件上下文窗口：事件前后各 3 个采样中与事件相关的读数子集。</summary>
    public sealed record TelemetryEventWindow(
        TelemetrySessionEvent Event,
        IReadOnlyList<TelemetryReading> Readings);

    /// <summary>
    /// 确定性分析摘要。刻意不包含任何 AI 字段——未来 AI 层通过
    /// schema version 追加，而不是现在占位污染（§48）。
    /// </summary>
    public sealed record TelemetrySessionSummary(
        TimeSpan Duration,
        int SampleCount,
        int RequestedIntervalMs,
        IReadOnlyList<MetricSeriesStatistic> Statistics,
        IReadOnlyList<TelemetrySessionEvent> TopEvents,
        IReadOnlyList<TelemetryEventWindow> EventWindows,
        IReadOnlyList<TelemetrySourceReport> FinalSources)
    {
    }

    public sealed record TelemetryRecordingSession(
        string Id,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        int RequestedIntervalMs,
        RecordingStatus Status,
        IReadOnlyList<TelemetrySample> Samples,
        IReadOnlyList<TelemetrySessionEvent> Events,
        TelemetrySessionSummary? Summary,
        IReadOnlyList<TelemetrySourceReport> InitialSources)
    {
        /// <summary>录制期间向会话追加采样（底层为可变列表，序列化只读）。</summary>
        public void AddSample(TelemetrySample sample)
        {
            if (Samples is List<TelemetrySample> list)
            {
                list.Add(sample);
            }
        }

        public void AddEvent(TelemetrySessionEvent @event)
        {
            if (Events is List<TelemetrySessionEvent> list)
            {
                list.Add(@event);
            }
        }

        public void SetInitialSources(IReadOnlyList<TelemetrySourceReport> sources)
        {
            if (InitialSources is List<TelemetrySourceReport> list && list.Count == 0)
            {
                list.AddRange(sources);
            }
        }

        public static TelemetryRecordingSession Start(int intervalMs, DateTimeOffset startedAtUtc) =>
            new(
                Guid.NewGuid().ToString("N"),
                startedAtUtc,
                null,
                intervalMs,
                RecordingStatus.Recording,
                new List<TelemetrySample>(),
                new List<TelemetrySessionEvent>(),
                null,
                new List<TelemetrySourceReport>());
    }
}
