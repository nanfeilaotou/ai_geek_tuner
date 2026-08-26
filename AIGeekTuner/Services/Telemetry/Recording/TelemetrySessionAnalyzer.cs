using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    /// <summary>
    /// 确定性会话分析器（§15）：不调用 AI，不做任何“硬件是否故障”的判断。
    /// SignificantChange 等事件只是变化检测阈值（§18/§19），不是安全阈值。
    /// 百分位算法统一采用最近秩 nearest-rank（升序取第 ⌈p·n⌉ 个）。
    /// </summary>
    public static class TelemetrySessionAnalyzer
    {
        public const int MaxTopEvents = 20;
        public const int EventWindowHalfSize = 3;

        private sealed record ChangeRule(double AbsoluteDelta, double RelativeDelta);

        // §19 变化检测规则表：唯一权威定义。
        private static readonly IReadOnlyList<(TelemetryUnit Unit, ChangeRule Rule)> ChangeRules =
        [
            (TelemetryUnit.Celsius, new ChangeRule(5, 0)),
            (TelemetryUnit.Percent, new ChangeRule(30, 0)),
            (TelemetryUnit.Megahertz, new ChangeRule(500, 0.20)),
            (TelemetryUnit.Watt, new ChangeRule(25, 0.25)),
        ];

        public static TelemetrySessionSummary Analyze(TelemetryRecordingSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            var totalSamples = session.Samples.Count;
            var series = BuildSeries(session);
            var statistics = series.Values
                .Select(items => BuildStatistic(items, totalSamples))
                .OrderBy(statistic => statistic.DeviceKey)
                .ThenBy(statistic => statistic.MetricKey)
                .ToArray();

            var detected = new List<(TelemetrySessionEvent Event, double Weight, int SampleIndex)>();
            detected.AddRange(session.Events
                .Where(@event => @event.Type
                    is TelemetrySessionEventType.SampleGap
                    or TelemetrySessionEventType.SourceStateChanged
                    or TelemetrySessionEventType.SourceUnavailable)
                .Select(@event => (@event, 80d, FindSampleIndex(session, @event.TimestampUtc))));

            foreach (var items in series.Values)
            {
                DetectSignificantChanges(items, totalSamples, detected);
            }

            if (series.TryGetValue(
                    SeriesKey(TelemetryDeviceIdentity.Cpu("cpu"), TelemetryMetricKey.CpuThrottling),
                    out var throttleItems))
            {
                DetectThrottle(throttleItems, totalSamples, detected);
            }

            var topEvents = detected
                .OrderByDescending(entry => entry.Weight)
                .ThenBy(entry => entry.SampleIndex)
                .Take(MaxTopEvents)
                .Select(entry => entry.Event)
                .ToArray();
            var windows = BuildWindows(session, topEvents);
            var duration = (session.CompletedAtUtc ?? session.StartedAtUtc) - session.StartedAtUtc;

            return new TelemetrySessionSummary(
                Duration: duration,
                SampleCount: totalSamples,
                RequestedIntervalMs: session.RequestedIntervalMs,
                Statistics: statistics,
                TopEvents: topEvents,
                EventWindows: windows,
                FinalSources: session.InitialSources);
        }

        private static string SeriesKey(TelemetryDeviceIdentity device, TelemetryMetricKey metric) =>
            $"{device.DeviceKey}{metric.Value}";

        private static Dictionary<string, SeriesItems> BuildSeries(TelemetryRecordingSession session)
        {
            var series = new Dictionary<string, SeriesItems>(StringComparer.Ordinal);
            foreach (var sample in session.Samples)
            {
                foreach (var reading in sample.Readings)
                {
                    var key = SeriesKey(reading.Device, reading.MetricKey);
                    if (!series.TryGetValue(key, out var items))
                    {
                        items = new SeriesItems(reading.Device, reading.MetricKey, reading.Unit);
                        series[key] = items;
                    }

                    items.Points.Add((sample.Sequence, sample.CapturedAtUtc, reading.Value, reading.Source));
                }
            }

            return series;
        }

        internal static MetricSeriesStatistic BuildStatistic(SeriesItems items, int totalSamples)
        {
            var values = items.Points.Select(point => point.Value).OrderBy(value => value).ToArray();
            var average = values.Length > 0 ? values.Average() : 0;

            return new MetricSeriesStatistic(
                items.Device.DeviceKey,
                items.Device.DisplayName,
                items.MetricKey.Value,
                items.Unit.ToString(),
                values.Length,
                CoveragePercent(values.Length, totalSamples),
                values.Length > 0 ? values[0] : 0,
                values.Length > 0 ? values[^1] : 0,
                average,
                values.Length > 0 ? PercentileNearestRank(values, 0.50) : 0,
                values.Length > 0 ? PercentileNearestRank(values, 0.95) : 0,
                values.Length > 0 ? PercentileNearestRank(values, 0.99) : 0,
                items.Points.Count > 0 ? items.Points[0].AtUtc : default,
                items.Points.Count > 0 ? items.Points[^1].AtUtc : default);
        }

        /// <summary>Coverage = 出现该指标的采样数 / 会话总采样数（§17）。</summary>
        public static double CoveragePercent(int presentSamples, int totalSamples) =>
            totalSamples <= 0 ? 0 : Math.Min(100d, presentSamples * 100d / totalSamples);

        public static double PercentileNearestRank(IReadOnlyList<double> orderedAscending, double percentile)
        {
            if (orderedAscending.Count == 0)
            {
                throw new ArgumentException("Empty sequence.", nameof(orderedAscending));
            }

            var rank = (int)Math.Ceiling(percentile * orderedAscending.Count);
            if (rank < 1) rank = 1;
            if (rank > orderedAscending.Count) rank = orderedAscending.Count;
            return orderedAscending[rank - 1];
        }

        private static void DetectSignificantChanges(
            SeriesItems items,
            int totalSamples,
            ICollection<(TelemetrySessionEvent Event, double Weight, int SampleIndex)> detected)
        {
            var rule = ChangeRules.FirstOrDefault(candidate => candidate.Unit == items.Unit).Rule;
            if (rule is null || items.Points.Count < 2)
            {
                return;
            }

            for (var i = 1; i < items.Points.Count; i++)
            {
                var previous = items.Points[i - 1];
                var current = items.Points[i];
                if (current.AtUtc == previous.AtUtc)
                {
                    continue;
                }

                var delta = Math.Abs(current.Value - previous.Value);
                var threshold = Math.Max(
                    rule.AbsoluteDelta,
                    Math.Abs(previous.Value) * rule.RelativeDelta);
                if (delta >= threshold)
                {
                    detected.Add((
                        new TelemetrySessionEvent(
                            TelemetrySessionEventType.SignificantChange,
                            current.AtUtc,
                            current.Source.ToString(),
                            items.MetricKey.Value,
                            items.Device.DeviceKey,
                            FormatValue(previous.Value, items.Unit),
                            FormatValue(current.Value, items.Unit),
                            $"变化检测触发（阈值 {threshold:0.#} {items.Unit}）——非故障判定"),
                        Math.Min(50, delta / Math.Max(threshold, 1e-9) * 25),
                        IndexOfSequence(items, current.Sequence)));
                }
            }
        }

        private static void DetectThrottle(
            SeriesItems items,
            int totalSamples,
            ICollection<(TelemetrySessionEvent Event, double Weight, int SampleIndex)> detected)
        {
            var wasThrottling = false;
            foreach (var point in items.Points)
            {
                var isThrottling = point.Value > 0;
                if (isThrottling && !wasThrottling)
                {
                    detected.Add((
                        new TelemetrySessionEvent(
                            TelemetrySessionEventType.ThrottleObserved,
                            point.AtUtc,
                            point.Source.ToString(),
                            items.MetricKey.Value,
                            items.Device.DeviceKey,
                            string.Empty,
                            FormatValue(point.Value, TelemetryUnit.Percent),
                            "来源直接报告 CPU throttling > 0（上升沿去重）——非故障判定"),
                        100,
                        IndexOfSequence(items, point.Sequence)));
                }

                wasThrottling = isThrottling;
            }
        }

        private static IReadOnlyList<TelemetryEventWindow> BuildWindows(
            TelemetryRecordingSession session,
            IReadOnlyList<TelemetrySessionEvent> topEvents)
        {
            var windows = new List<TelemetryEventWindow>();
            foreach (var @event in topEvents)
            {
                var center = FindSampleIndex(session, @event.TimestampUtc);
                if (center < 0)
                {
                    continue;
                }

                var from = Math.Max(0, center - EventWindowHalfSize);
                var to = Math.Min(session.Samples.Count - 1, center + EventWindowHalfSize);
                var readings = new List<TelemetryReading>();
                for (var i = from; i <= to; i++)
                {
                    foreach (var reading in session.Samples[i].Readings)
                    {
                        var sameDevice = !string.IsNullOrEmpty(@event.DeviceKey)
                            && string.Equals(reading.Device.DeviceKey, @event.DeviceKey, StringComparison.Ordinal);
                        var globalCore = reading.Device.Kind
                            is TelemetryDeviceKind.Cpu or TelemetryDeviceKind.Gpu;
                        if (string.IsNullOrEmpty(@event.DeviceKey) || sameDevice || globalCore)
                        {
                            readings.Add(reading);
                        }
                    }
                }

                windows.Add(new TelemetryEventWindow(@event, readings));
            }

            return windows;
        }

        private static int FindSampleIndex(TelemetryRecordingSession session, DateTimeOffset timestamp)
        {
            for (var i = 0; i < session.Samples.Count; i++)
            {
                if (Math.Abs((session.Samples[i].CapturedAtUtc - timestamp).TotalMilliseconds) <= 5)
                {
                    return i;
                }
            }

            var last = -1;
            for (var i = 0; i < session.Samples.Count; i++)
            {
                if (session.Samples[i].CapturedAtUtc <= timestamp)
                {
                    last = i;
                }
                else
                {
                    break;
                }
            }

            return last;
        }

        private static int IndexOfSequence(SeriesItems items, int sequence)
        {
            var index = items.Points.FindIndex(point => point.Sequence == sequence);
            return index < 0 ? 0 : index;
        }

        /// <summary>统计构建的公开入口（UI/测试使用；Hub/Recorder 内部亦走此路径）。</summary>
        public static MetricSeriesStatistic BuildSeriesStatistic(
            TelemetryDeviceIdentity device,
            TelemetryMetricKey metricKey,
            TelemetryUnit unit,
            IReadOnlyList<(int Sequence, DateTimeOffset AtUtc, double Value, TelemetrySourceKind Source)> points,
            int totalSamples)
        {
            var items = new SeriesItems(device, metricKey, unit);
            items.Points.AddRange(points);
            return BuildStatistic(items, totalSamples);
        }

        public static string FormatValue(double value, TelemetryUnit unit) =>
            unit switch
            {
                TelemetryUnit.Celsius => $"{value:0.#} °C",
                TelemetryUnit.Watt => $"{value:0.#} W",
                TelemetryUnit.Megahertz => $"{value:0} MHz",
                TelemetryUnit.Percent => $"{value:0.#} %",
                TelemetryUnit.Volt => $"{value:0.###} V",
                TelemetryUnit.Byte => $"{value / 1073741824d:0.##} GB",
                _ => $"{value:0.###}"
            };

        internal sealed class SeriesItems
        {
            public SeriesItems(
                TelemetryDeviceIdentity device,
                TelemetryMetricKey metricKey,
                TelemetryUnit unit)
            {
                Device = device;
                MetricKey = metricKey;
                Unit = unit;
            }

            public TelemetryDeviceIdentity Device { get; }

            public TelemetryMetricKey MetricKey { get; }

            public TelemetryUnit Unit { get; }

            public List<(int Sequence, DateTimeOffset AtUtc, double Value, TelemetrySourceKind Source)>
                Points { get; } = [];
        }

        // ---- 未来 AI 层唯一输入：压缩后的结构化上下文（§24），不含全部 samples。----

        public sealed record TelemetryAnalysisContext(
            AnalysisMetadata Metadata,
            IReadOnlyList<AnalysisSource> Sources,
            IReadOnlyList<AnalysisStatistic> Statistics,
            IReadOnlyList<AnalysisEvent> Events);

        public sealed record AnalysisMetadata(
            string SessionId,
            DateTimeOffset StartedAtUtc,
            TimeSpan Duration,
            int SampleCount,
            int IntervalMs);

        public sealed record AnalysisSource(string Source, string Status, int RawReadings);

        public sealed record AnalysisStatistic(
            string DeviceKey,
            string MetricKey,
            string Unit,
            int Samples,
            double CoveragePercent,
            double Min,
            double Avg,
            double Max,
            double P95);

        public sealed record AnalysisEvent(
            string Type,
            DateTimeOffset TimestampUtc,
            string Source,
            string MetricKey,
            string From,
            string To,
            string Detail);

        private static readonly JsonSerializerOptions ContextOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static TelemetryAnalysisContext BuildAnalysisContext(TelemetryRecordingSession session)
        {
            // 事件窗口体积较大且面向 UI/后续深挖；AI 压缩上下文只含 TopEvents 本身。
            var summary = session.Summary ?? Analyze(session);
            return new TelemetryAnalysisContext(
                new AnalysisMetadata(
                    session.Id,
                    session.StartedAtUtc,
                    summary.Duration,
                    summary.SampleCount,
                    summary.RequestedIntervalMs),
                summary.FinalSources.Select(source => new AnalysisSource(
                    source.Source.ToString(),
                    source.Status.ToString(),
                    source.RawReadingCount)).ToArray(),
                summary.Statistics.Select(statistic => new AnalysisStatistic(
                    statistic.DeviceKey,
                    statistic.MetricKey,
                    statistic.Unit,
                    statistic.SampleCount,
                    Math.Round(statistic.CoveragePercent, 1),
                    Math.Round(statistic.Minimum, 2),
                    Math.Round(statistic.Average, 2),
                    Math.Round(statistic.Maximum, 2),
                    Math.Round(statistic.P95, 2))).ToArray(),
                summary.TopEvents.Select(@event => new AnalysisEvent(
                    @event.Type.ToString(),
                    @event.TimestampUtc,
                    @event.Source ?? string.Empty,
                    @event.MetricKey ?? string.Empty,
                    @event.From,
                    @event.To,
                    @event.Detail)).ToArray());
        }

        public static string SerializeAnalysisContext(TelemetryAnalysisContext context) =>
            JsonSerializer.Serialize(context, ContextOptions);
    }
}
