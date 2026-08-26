using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording
{
    /// <summary>§42 录制引擎：确定性驱动 CaptureOnce，不做真实长等待。</summary>
    public class TelemetryRecordingServiceTests
    {
        private static readonly DateTimeOffset T0 = new(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);

        private sealed class FakeHub(Queue<TelemetrySnapshot> snapshots) : ITelemetryHub
        {
            public int CallCount { get; private set; }

            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default)
            {
                CallCount++;
                if (snapshots.Count == 0)
                {
                    throw new InvalidOperationException("hub exhausted");
                }

                return Task.FromResult(snapshots.Dequeue());
            }
        }

        private static TelemetryReading Reading(
            string deviceKey, string metric, double value,
            TelemetrySourceKind source = TelemetrySourceKind.HwInfo,
            TelemetryDeviceKind kind = TelemetryDeviceKind.Cpu) =>
            new(
                new TelemetryMetricKey(metric),
                value,
                metric.Contains("temperature") ? TelemetryUnit.Celsius
                    : metric.Contains("utilization") || metric.Contains("throttling") ? TelemetryUnit.Percent
                    : metric.Contains("clock") ? TelemetryUnit.Megahertz
                    : metric.Contains("power") ? TelemetryUnit.Watt
                    : TelemetryUnit.None,
                new TelemetryDeviceIdentity(kind, deviceKey, deviceKey),
                source,
                $"raw:{metric}",
                null,
                T0);

        private static TelemetryProviderResult Result(
            params TelemetryReading[] readings) =>
            new(TelemetrySourceStatus.Ready, "ok",
                readings.Select(r => new RawTelemetryReading(
                    r.Source, r.SourceMetricId!, "", r.Value, r.Unit, r.Device,
                    new SourceDeviceInfo(r.Source, r.Device.Kind, r.Device.DeviceKey,
                        r.Device.DisplayName, 0, []), T0)).ToArray(),
                readings, T0);

        [Fact]
        public async Task Start_RejectsDuplicateAndInvalidInterval()
        {
            var hub = new FakeHub(new Queue<TelemetrySnapshot>());
            var service = new TelemetryRecordingService(hub);

            Assert.False(service.Start(100));   // 低于下限
            Assert.False(service.Start(10_000)); // 超过上限

            Assert.True(service.Start(200));
            Assert.False(service.Start(200));    // 单活动会话（§35）
            await service.StopAsync();
        }

        [Fact]
        public async Task CaptureOnce_CollectsSample_WithRealTimestampAndDuration()
        {
            var queue = new Queue<TelemetrySnapshot>();
            queue.Enqueue(new TelemetrySnapshot(T0, [
                Reading("cpu", "cpu.package.temperature", 71.5)], [], []));
            var service = new TelemetryRecordingService(new FakeHub(queue));
            var session = TelemetryRecordingSession.Start(2000, T0);

            var sample = await service.CaptureOnceAsync(session, CancellationToken.None);

            Assert.Equal(1, sample.Sequence);
            Assert.Single(sample.Readings);
            Assert.True(sample.CapturedAtUtc >= T0);
        }

        [Fact]
        public async Task ProviderFailure_ContinuesAndRecordsGap()
        {
            var queue = new Queue<TelemetrySnapshot>();
            queue.Enqueue(new TelemetrySnapshot(T0, [], [], [])); // 空快照 = gap
            queue.Enqueue(new TelemetrySnapshot(T0.AddSeconds(2), [
                Reading("cpu", "cpu.total.utilization", 30)], [], []));
            var hub = new FakeHub(queue);
            var service = new TelemetryRecordingService(hub);
            var session = TelemetryRecordingSession.Start(2000, T0);

            var first = await service.CaptureOnceAsync(session, CancellationToken.None);
            var second = await service.CaptureOnceAsync(session, CancellationToken.None);

            Assert.Empty(first.Readings);
            Assert.Single(second.Readings);
            // gap 事件已入会话（§37）
            Assert.Contains(session.Events, e => e.Type == TelemetrySessionEventType.SampleGap);
        }

        [Fact]
        public async Task SourceTransition_EmitsStateChangedEvent_OnlyOnChange()
        {
            var firstReports = new List<TelemetrySourceReport>
            {
                new(TelemetrySourceKind.HwInfo, TelemetrySourceStatus.Ready, "ok", 3, T0),
            };
            var secondReports = new List<TelemetrySourceReport>
            {
                new(TelemetrySourceKind.HwInfo, TelemetrySourceStatus.Unavailable, "gone", 0, null),
                new(TelemetrySourceKind.LibreHardwareMonitor, TelemetrySourceStatus.Ready, "ok", 5, T0),
            };
            var queue = new Queue<TelemetrySnapshot>();
            queue.Enqueue(new TelemetrySnapshot(T0, [], firstReports, []));
            queue.Enqueue(new TelemetrySnapshot(T0.AddSeconds(2), [], secondReports, []));
            var service = new TelemetryRecordingService(new FakeHub(queue));
            var session = TelemetryRecordingSession.Start(2000, T0);

            _ = await service.CaptureOnceAsync(session, CancellationToken.None);
            _ = await service.CaptureOnceAsync(session, CancellationToken.None);

            var dbg = string.Join(" | ", session.Events.Select(e =>
                e.Type + ":" + e.Source + ":" + e.From + ">" + e.To));
            var transitions = session.Events.Where(e =>
                e.Source == "HwInfo"
                && e.Type is TelemetrySessionEventType.SourceStateChanged
                    or TelemetrySessionEventType.SourceUnavailable).ToArray();
            Assert.True(transitions.Length == 1, "events=[" + dbg + "]");
            var transition = transitions[0];
            Assert.Equal("Ready", transition.From);
            Assert.Equal("Unavailable", transition.To);
        }

        [Fact]
        public async Task MetricSourceChange_EmitsDedupedEvent()
        {
            var gpu = TelemetryDeviceIdentity.GpuByIndex(0, "GPU");
            var queue = new Queue<TelemetrySnapshot>();
            queue.Enqueue(new TelemetrySnapshot(T0, [Reading("gpu:0", "gpu.core.temperature", 60)], [], []));
            queue.Enqueue(new TelemetrySnapshot(T0.AddSeconds(2), [
                Reading("gpu:0", "gpu.core.temperature", 61, TelemetrySourceKind.Aida64)], [], []));
            queue.Enqueue(new TelemetrySnapshot(T0.AddSeconds(4), [
                Reading("gpu:0", "gpu.core.temperature", 62, TelemetrySourceKind.Aida64)], [], []));
            var service = new TelemetryRecordingService(new FakeHub(queue));
            var session = TelemetryRecordingSession.Start(2000, T0);

            _ = await service.CaptureOnceAsync(session, CancellationToken.None);
            _ = await service.CaptureOnceAsync(session, CancellationToken.None);
            _ = await service.CaptureOnceAsync(session, CancellationToken.None);

            var events = session.Events.Where(e =>
                e.Type == TelemetrySessionEventType.MetricSourceChanged).ToArray();
            var single = Assert.Single(events); // 第三轮同源不再重复（§9 去重）
            Assert.Equal("HwInfo", single.From);
            Assert.Equal("Aida64", single.To);
        }

        [Fact]
        public async Task MaxSamplesReached_AutoStops()
        {
            var snapshots = Enumerable.Range(0, 50).Select(i => new TelemetrySnapshot(
                T0.AddMilliseconds(i * 10),
                new List<TelemetryReading> { Reading("cpu", "cpu.total.utilization", i) },
                [],
                [])).ToQueue();
            var service = new TelemetryRecordingService(new FakeHub(snapshots), maxSamplesOverride: 5);

            Assert.True(service.Start(200));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (service.IsRecording && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.False(service.IsRecording); // 自动正常收尾（§14）
            var session = service.CurrentSession!;
            Assert.Equal(RecordingStatus.Completed, session.Status);
            Assert.NotNull(session.Summary);
            Assert.True(session.Samples.Count >= 5);
        }

        [Fact]
        public async Task Stop_CompletesWithSummary_AnalyzerWired()
        {
            var queue = new Queue<TelemetrySnapshot>();
            for (var i = 0; i < 3; i++)
            {
                queue.Enqueue(new TelemetrySnapshot(T0.AddSeconds(i * 2),
                    new List<TelemetryReading> { Reading("cpu", "cpu.total.utilization", 40 + i) },
                    new List<TelemetrySourceReport>
                    {
                        new(TelemetrySourceKind.HwInfo, TelemetrySourceStatus.Ready, "ok", 1, T0),
                    },
                    []));
            }

            var service = new TelemetryRecordingService(new FakeHub(queue));
            Assert.True(service.Start(200));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (service.CurrentSession!.Samples.Count < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            var finalized = await service.StopAsync();

            Assert.NotNull(finalized);
            Assert.Equal(RecordingStatus.Completed, finalized!.Status);
            Assert.NotNull(finalized.Summary);
            Assert.Equal(3, finalized.Summary.SampleCount);
            Assert.All(finalized.Summary.Statistics, s => Assert.Equal(100, s.CoveragePercent));
        }
    }

    internal static class RecordingTestExtensions
    {
        public static Queue<T> ToQueue<T>(this IEnumerable<T> source) => new(source);
    }
}

