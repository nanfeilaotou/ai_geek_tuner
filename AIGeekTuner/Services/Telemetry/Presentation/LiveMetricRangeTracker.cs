using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Presentation
{
    /// <summary>单一 canonical 指标在本次实时监测期间观察到的范围。</summary>
    public sealed record LiveMetricRange(
        double Current,
        double? Low,
        double? High);

    /// <summary>
    /// V2-M4.5C Gate D：来源无关的实时范围跟踪。
    /// 按 canonical (DeviceKey, MetricKey) 维护 Current/Low/High——
    /// 绝不使用 HWiNFO native Min/Max（Hub 可能发生 HWiNFO→AIDA64→LHM
    /// fallback，AIGeekTuner 的范围必须与来源无关且连续）。
    /// 语义：Low/High 是"本次实时监测期间 AIGeekTuner 观察到的范围"，
    /// 不是硬件历史值，也不是 Recorder 的 Session statistics（保持独立）。
    /// 线程安全：snapshot 更新来自 UI 调度线程，仍以锁保护。
    /// </summary>
    public sealed class LiveMetricRangeTracker
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string DeviceKey, string MetricKey), (double Low, double High, double Current)> _ranges = new();

        /// <summary>实时监测开始或用户点击"重置范围"：清空全部 Low/High。</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _ranges.Clear();
            }
        }

        /// <summary>消费一个 canonical snapshot：更新每个 (DeviceKey, MetricKey) 的范围。</summary>
        public void Update(TelemetrySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            lock (_gate)
            {
                foreach (var reading in snapshot.CanonicalReadings)
                {
                    var key = (reading.Device.DeviceKey, reading.MetricKey.Value);
                    if (_ranges.TryGetValue(key, out var range))
                    {
                        _ranges[key] = (
                            Math.Min(range.Low, reading.Value),
                            Math.Max(range.High, reading.Value),
                            reading.Value);
                    }
                    else
                    {
                        _ranges[key] = (reading.Value, reading.Value, reading.Value);
                    }
                }
            }
        }

        /// <summary>取当前值与观察范围；该指标本会话未观察到时返回 null。</summary>
        public LiveMetricRange? GetRange(string deviceKey, TelemetryMetricKey metricKey)
        {
            lock (_gate)
            {
                if (!_ranges.TryGetValue((deviceKey, metricKey.Value), out var range))
                {
                    return null;
                }

                return new LiveMetricRange(range.Current, range.Low, range.High);
            }
        }

        /// <summary>
        /// 取"当前所有 resolved 读数中"某指标的最大值对应设备（如内存温度摘要）。
        /// 没有该指标读数时返回 null。
        /// </summary>
        public LiveMetricRange? GetHighestAcrossDevices(TelemetrySnapshot snapshot, TelemetryMetricKey metricKey)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var latest = snapshot.CanonicalReadings
                .Where(reading => reading.MetricKey == metricKey)
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (latest.Length == 0)
            {
                return null;
            }

            lock (_gate)
            {
                LiveMetricRange? best = null;
                foreach (var reading in latest)
                {
                    if (_ranges.TryGetValue((reading.Device.DeviceKey, metricKey.Value), out var range))
                    {
                        if (best is null || range.High > best.High)
                        {
                            best = new LiveMetricRange(range.Current, range.Low, range.High);
                        }
                    }
                }

                return best;
            }
        }
    }
}
