using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// 短期内存趋势缓冲（§25-§27）：仅 canonical 读数，按时间窗口保留，不持久化。
    /// 来源切换不断开同一条趋势线，只在点上记录来源（§37）。
    /// </summary>
    public sealed class TelemetryTrendBuffer
    {
        public readonly record struct TrendPoint(DateTimeOffset AtUtc, double Value, TelemetrySourceKind Source);

        private readonly object _gate = new();
        private readonly TimeSpan _retention;
        private readonly int _maxPointsPerSeries;
        private readonly Dictionary<string, List<TrendPoint>> _series = new(StringComparer.Ordinal);

        public TelemetryTrendBuffer(TimeSpan? retention = null, int maxPointsPerSeries = 600)
        {
            _retention = retention ?? TimeSpan.FromMinutes(2);
            _maxPointsPerSeries = maxPointsPerSeries;
        }

        public void AddSnapshot(TelemetrySnapshot snapshot)
        {
            lock (_gate)
            {
                foreach (var reading in snapshot.CanonicalReadings)
                {
                    if (!double.IsFinite(reading.Value))
                    {
                        continue; // NaN/Inf 上游拒绝（§32）
                    }

                    var key = deviceKeyOf(reading) + "" + reading.MetricKey.Value;
                    if (!_series.TryGetValue(key, out var points))
                    {
                        points = new List<TrendPoint>();
                        _series[key] = points;
                    }

                    points.Add(new TrendPoint(
                        reading.CapturedAtUtc,
                        reading.Value,
                        reading.Source));
                }

                Trim(snapshot.CapturedAtUtc);
            }
        }

        private static string deviceKeyOf(TelemetryReading r) => r.Device.DeviceKey;

        private void Trim(DateTimeOffset nowUtc)
        {
            var cutoff = nowUtc - _retention;
            foreach (var key in _series.Keys.ToArray())
            {
                var points = _series[key];
                points.RemoveAll(p => p.AtUtc < cutoff || p.AtUtc > nowUtc);
                while (points.Count > _maxPointsPerSeries)
                {
                    points.RemoveAt(0);
                }

                if (points.Count == 0)
                {
                    _series.Remove(key);
                }
            }
        }

        public IReadOnlyList<TrendPoint> GetSeries(string deviceKey, string metricKey)
        {
            lock (_gate)
            {
                return _series.TryGetValue(deviceKey + "" + metricKey, out var points)
                    ? points.ToArray()
                    : [];
            }
        }
    }
}


