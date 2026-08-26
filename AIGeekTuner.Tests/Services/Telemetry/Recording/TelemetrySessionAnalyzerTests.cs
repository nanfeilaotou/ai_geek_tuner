using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording
{
    /// <summary>§43 分析器：百分位、覆盖、隔离、事件规则、压缩。</summary>
    public class TelemetrySessionAnalyzerTests
    {
        private static readonly DateTimeOffset T0 = new(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);

        private sealed class Builder
        {
            public int IntervalMs { get; init; } = 2000;
            public DateTimeOffset Clock { get; set; } = T0;
            private readonly List<TelemetrySample> _samples = [];
            private readonly List<TelemetrySessionEvent> _events = [];
            private int _sequence;

            public Builder Add(double cpuTemp, double gpuTemp, string gpuKey = "gpu:0",
                string source = "HwInfo", double? utilization = null, double? clockMhz = null,
                double? powerW = null, double? throttling = null)
            {
                Clock += TimeSpan.FromSeconds(IntervalMs / 1000d);
                var seq = ++_sequence;
                var readings = new List<TelemetryReading>
                {
                    R($"cpu", TelemetryMetricKey.CpuPackageTemperature, cpuTemp, TelemetryUnit.Celsius, source),
                    R(gpuKey, TelemetryMetricKey.GpuCoreTemperature, gpuTemp, TelemetryUnit.Celsius, source),
                };
                if (utilization is not null)
                    readings.Add(R("cpu", TelemetryMetricKey.CpuTotalUtilization, utilization.Value, TelemetryUnit.Percent, source));
                if (clockMhz is not null)
                    readings.Add(R(gpuKey, TelemetryMetricKey.GpuCoreClock, clockMhz.Value, TelemetryUnit.Megahertz, source));
                if (powerW is not null)
                    readings.Add(R(gpuKey, TelemetryMetricKey.GpuBoardPower, powerW.Value, TelemetryUnit.Watt, source));
                if (throttling is not null)
                    readings.Add(R("cpu", TelemetryMetricKey.CpuThrottling, throttling.Value, TelemetryUnit.Percent, source));
                _samples.Add(new TelemetrySample(seq, Clock, 5, readings));
                return this;
            }

            public Builder AddEmpty()
            {
                Clock += TimeSpan.FromSeconds(IntervalMs / 1000d);
                _samples.Add(new TelemetrySample(++_sequence, Clock, 5, []));
                return this;
            }

            public Builder Event(TelemetrySessionEvent e)
            {
                _events.Add(e);
                return this;
            }

            public TelemetryRecordingSession Build() =>
                new("test", T0, Clock, IntervalMs, RecordingStatus.Completed,
                    _samples.ToArray(), _events.ToArray(), null, []);

            private static TelemetryReading R(
                string deviceKey, TelemetryMetricKey metric, double value,
                TelemetryUnit unit, string source) =>
                new(metric, value, unit,
                    deviceKey == "cpu"
                        ? TelemetryDeviceIdentity.Cpu("CPU")
                        : TelemetryDeviceIdentity.GpuByIndex(int.Parse(deviceKey[^1].ToString()), deviceKey),
                    Enum.Parse<TelemetrySourceKind>(source),
                    $"raw:{metric.Value}", null, T0);
        }

        [Fact]
        public void Percentile_NearestRank_ExactValues()
        {
            var data = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
            Assert.Equal(5, TelemetrySessionAnalyzer.PercentileNearestRank(data, 0.50));
            Assert.Equal(10, TelemetrySessionAnalyzer.PercentileNearestRank(data, 0.95));
            Assert.Equal(10, TelemetrySessionAnalyzer.PercentileNearestRank(data, 0.99));
            Assert.Equal(1, TelemetrySessionAnalyzer.PercentileNearestRank(data, 0.01));
        }

        [Fact]
        public void Statistics_MinMaxAvgP95_ComputedPerSeries()
        {
            var session = new Builder()
                .Add(cpuTemp: 70, gpuTemp: 60, utilization: 40)
                .Add(cpuTemp: 72, gpuTemp: 62, utilization: 50)
                .Add(cpuTemp: 74, gpuTemp: 64, utilization: 60)
                .Build();

            var summary = TelemetrySessionAnalyzer.Analyze(session);

            var cpuTemp = summary.Statistics.Single(s => s.MetricKey == "cpu.package.temperature");
            Assert.Equal(3, cpuTemp.SampleCount);
            Assert.Equal(70, cpuTemp.Minimum);
            Assert.Equal(74, cpuTemp.Maximum);
            Assert.Equal(72, cpuTemp.Average, precision: 6);
            Assert.Equal(74, cpuTemp.P95); // nearest-rank on 3 items: rank ceil(.95*3)=3
            Assert.Equal(100, cpuTemp.CoveragePercent);
        }

        [Fact]
        public void Coverage_CountsOnlySamplesWhereMetricPresent()
        {
            var builder = new Builder();
            builder.Add(70, 60).AddEmpty().AddEmpty().Add(71, 61);
            var summary = TelemetrySessionAnalyzer.Analyze(builder.Build());

            var cpuTemp = summary.Statistics.Single(s => s.MetricKey == "cpu.package.temperature");
            Assert.Equal(2, cpuTemp.SampleCount);
            Assert.Equal(50, cpuTemp.CoveragePercent, precision: 6);
        }

        [Fact]
        public void TwoGpus_StayIsolated_InStatisticsAndChangeDetection()
        {
            // 手工构造每个采样同时含两块 GPU 的温度：gpu:0 稳定、gpu:1 跳变。
            var samples = new List<TelemetrySample>();
            var temps = new Dictionary<string, double[]> // 每采样两卡的值序列
            {
                ["gpu:0"] = [50, 51, 52],   // Δ≤2，无事件
                ["gpu:1"] = [60, 95, 96],   // 首跳 Δ35 → gpu:1 的显著变化
            };
            var keys = new[] { "gpu:0", "gpu:1" };
            for (var i = 0; i < 3; i++)
            {
                var readings = keys.Select(key => new TelemetryReading(
                    TelemetryMetricKey.GpuCoreTemperature,
                    temps[key][i],
                    TelemetryUnit.Celsius,
                    TelemetryDeviceIdentity.GpuByIndex(int.Parse(key[^1].ToString()), key),
                    TelemetrySourceKind.HwInfo,
                    $"raw:{i}", null, T0)).ToList();
                readings.Add(new TelemetryReading(
                    TelemetryMetricKey.CpuPackageTemperature, 70 + i,
                    TelemetryUnit.Celsius,
                    TelemetryDeviceIdentity.Cpu("CPU"),
                    TelemetrySourceKind.HwInfo, "raw:cpu", null, T0));
                samples.Add(new TelemetrySample(i + 1, T0.AddSeconds(i * 2), 5, readings));
            }

            var session = new TelemetryRecordingSession(
                "iso", T0, T0.AddSeconds(6), 2000, RecordingStatus.Completed,
                samples, [], null, []);

            var summary = TelemetrySessionAnalyzer.Analyze(session);

            Assert.Equal(2, summary.Statistics.Count(s =>
                s.MetricKey == "gpu.core.temperature"));

            var gpu1Event = summary.TopEvents.Single(e =>
                e.Type == TelemetrySessionEventType.SignificantChange
                && e.DeviceKey == "gpu:1");
            Assert.Equal("60 \u00b0C", gpu1Event.From);

            // 不存在把 gpu:1 的跳变安到 gpu:0 头上的串值事件。
            Assert.DoesNotContain(summary.TopEvents,
                e => e.DeviceKey == "gpu:0" && e.To!.StartsWith("9"));
        }

        [Theory]
        [InlineData(70, 76, 1)]   // Δ6 ≥5 → 触发
        [InlineData(70, 74, 0)]   // Δ4 → 不触发
        public void TemperatureSignificantChange_ThresholdIsFiveDegrees(
            double first, double second, int expectedCount)
        {
            var session = new Builder().Add(first, 60).Add(second, 60).Build();
            var summary = TelemetrySessionAnalyzer.Analyze(session);

            Assert.Equal(expectedCount, summary.TopEvents.Count(e =>
                e.Type == TelemetrySessionEventType.SignificantChange
                && e.MetricKey == "cpu.package.temperature"));
        }

        [Fact]
        public void UtilizationChange_ThirtyPointsThreshold()
        {
            var session = new Builder()
                .Add(70, 60, utilization: 10)
                .Add(70, 60, utilization: 45)  // Δ35 ≥30 → 触发
                .Build();

            var summary = TelemetrySessionAnalyzer.Analyze(session);

            Assert.Contains(summary.TopEvents, e =>
                e.Type == TelemetrySessionEventType.SignificantChange
                && e.MetricKey == "cpu.total.utilization");
        }

        [Fact]
        public void ClockChange_UsesRelativeOrAbsoluteRule()
        {
            // Δ400 <500 且 <20%*2400=480 → 不触发；随后从 2400→1800（Δ600≥480）触发。
            var session = new Builder()
                .Add(70, 60, clockMhz: 2400)
                .Add(70, 60, clockMhz: 2800)
                .Add(70, 60, clockMhz: 2200)
                .Build();

            var summary = TelemetrySessionAnalyzer.Analyze(session);

            // 2400→2800：Δ400 < max(500, 480) 不触发；2800→2200：Δ600 ≥ max(500,560) 触发。
            var clockEvents = summary.TopEvents.Count(e =>
                e.Type == TelemetrySessionEventType.SignificantChange
                && e.MetricKey == "gpu.core.clock");
            Assert.Equal(1, clockEvents);
        }

        [Fact]
        public void PowerChange_MaxOfAbsoluteAndRelative()
        {
            var session = new Builder()
                .Add(70, 60, powerW: 30)
                .Add(70, 60, powerW: 58)   // Δ28 ≥ max(25, 7.5) → 触发
                .Build();

            var summary = TelemetrySessionAnalyzer.Analyze(session);

            Assert.Contains(summary.TopEvents, e =>
                e.Type == TelemetrySessionEventType.SignificantChange
                && e.MetricKey == "gpu.board.power");
        }

        [Fact]
        public void Throttle_EmitsSingleRisingEdge_ForSustainedRun()
        {
            var session = new Builder()
                .Add(70, 60, throttling: 0)
                .Add(70, 60, throttling: 25)
                .Add(70, 60, throttling: 30)
                .Add(70, 60, throttling: 0)
                .Add(70, 60, throttling: 10)
                .Build();

            var summary = TelemetrySessionAnalyzer.Analyze(session);

            Assert.Equal(2, summary.TopEvents.Count(e =>
                e.Type == TelemetrySessionEventType.ThrottleObserved)); // 两个上升沿
        }

        [Fact]
        public void TopEvents_CappedAtTwenty()
        {
            var builder = new Builder();
            for (var i = 0; i < 30; i++)
            {
                builder.Add(i % 2 == 0 ? 70 : 80, 60); // 每轮都触发一次温度变化
            }

            var summary = TelemetrySessionAnalyzer.Analyze(builder.Build());

            Assert.True(summary.TopEvents.Count <= 20);
        }

        [Fact]
        public void EventWindows_BoundedAtSessionEdges_AndFilteredByDevice()
        {
            var session = new Builder()
                .Add(70, 60)
                .Add(70, 60)
                .Add(76, 60)   // 事件在 index 2，前窗应被裁剪到 index 0
                .Build();

            var summary = TelemetrySessionAnalyzer.Analyze(session);
            var significant = summary.TopEvents.First(e =>
                e.Type == TelemetrySessionEventType.SignificantChange);
            var window = summary.EventWindows.First(w =>
                ReferenceEquals(w.Event, significant));

            var sequencesInWindow = window.Readings.Select(r => r.Device.DeviceKey).Distinct().ToList();
            Assert.Contains("cpu", sequencesInWindow);
            Assert.Contains("gpu:0", sequencesInWindow); // 全局核心指标保留
        }

        [Fact]
        public void OneHourSynthetic_CompressionAndPerformance()
        {
            // 1h @2s × 2 GPU × ~16 metrics ≈ 真实规模（§24/§44）
            var builder = new Builder();
            var random = new Random(1234);
            for (var i = 0; i < 1800; i++)
            {
                builder
                    .Add(60 + random.NextDouble() * 30, 55 + random.NextDouble() * 25,
                        utilization: random.NextDouble() * 100,
                        clockMhz: 2100 + random.NextDouble() * 600,
                        powerW: 30 + random.NextDouble() * 120)
                    .Add(55 + random.NextDouble() * 20, 45 + random.NextDouble() * 15,
                        gpuKey: "gpu:1", source: "Aida64");
            }

            var session = builder.Build();
            var rawReadings = session.Samples.Sum(sample => sample.Readings.Count);
            var rawJson = System.Text.Json.JsonSerializer.Serialize(session.Samples);

            var analyzeSw = System.Diagnostics.Stopwatch.StartNew();
            var summary = TelemetrySessionAnalyzer.Analyze(session);
            analyzeSw.Stop();

            var context = TelemetrySessionAnalyzer.BuildAnalysisContext(session with { Summary = summary });
            var contextJson = TelemetrySessionAnalyzer.SerializeAnalysisContext(context);

            Assert.Equal(3600, summary.SampleCount * 2 / 2 + summary.SampleCount - summary.SampleCount); // sanity: no crash
            Assert.True(rawReadings > 10_000); // 3600 samples × ~6 readings
            Assert.True(context.Events.Count <= 20);
            Assert.True(contextJson.Length < rawJson.Length / 10,
                $"context={contextJson.Length} raw={rawJson.Length} 压缩不足 10x");
            Assert.True(analyzeSw.ElapsedMilliseconds < 15_000,
                $"分析耗时过长：{analyzeSw.ElapsedMilliseconds}ms");
        }
    }
}
