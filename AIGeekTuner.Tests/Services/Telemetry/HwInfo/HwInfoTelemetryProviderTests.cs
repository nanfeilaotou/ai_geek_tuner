using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.HwInfo;

namespace AIGeekTuner.Tests.Services.Telemetry.HwInfo
{
    public class HwInfoTelemetryProviderTests
    {
        private sealed class StubReader(Func<HwInfoReaderOutcome> outcomeFactory)
            : IHwInfoSensorReader
        {
            public Task<HwInfoReaderOutcome> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(outcomeFactory());
        }

        private sealed class StubProcessDetector(bool running) : IHwInfoProcessDetector
        {
            public bool IsRunning() => running;
        }

        private static readonly HwInfoSensorEntry[] Sensors =
        [
            new HwInfoSensorEntry(0, "CPU [#0]: Intel Core"),
        ];

        [Fact]
        public async Task NoProcessAndNoSharedMemory_ReturnsUnavailable()
        {
            var provider = new HwInfoTelemetryProvider(
                new StubReader(() => HwInfoReaderOutcome.SharedMemoryNotAvailable("n/a")),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Unavailable, result.Status);
        }

        [Fact]
        public async Task ProcessRunningWithoutSharedMemory_ReturnsNeedsConfiguration()
        {
            var provider = new HwInfoTelemetryProvider(
                new StubReader(() => HwInfoReaderOutcome.SharedMemoryNotAvailable("n/a")),
                new StubProcessDetector(true));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.NeedsConfiguration, result.Status);
            Assert.Contains("Shared Memory Support", result.Message);
            Assert.Contains("12 小时", result.Message);
        }

        [Fact]
        public async Task ReaderFailure_ReturnsErrorWithoutThrowing()
        {
            var provider = new HwInfoTelemetryProvider(
                new StubReader(() => HwInfoReaderOutcome.ReadFailed("bad mapping")),
                new StubProcessDetector(true));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Error, result.Status);
        }

        [Fact]
        public async Task AvailableSnapshot_ProducesReadyWithRawAndCanonical()
        {
            var provider = new HwInfoTelemetryProvider(
                new StubReader(() => HwInfoReaderOutcome.Snapshot(
                    Sensors,
                    [
                        new HwInfoReadingEntry(0, 1, "CPU Package", "°C", 70),
                        new HwInfoReadingEntry(0, 2, "CPU Total", "%", 22),
                    ])),
                new StubProcessDetector(true));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Ready, result.Status);
            Assert.Equal(2, result.RawReadings.Count);
            Assert.Equal(2, result.CanonicalReadings.Count);
            Assert.All(result.RawReadings, reading =>
                Assert.Equal(TelemetrySourceKind.HwInfo, reading.Source));
        }

        [Fact]
        public async Task SharedMemoryDisappearThenReturn_StatusFollowsLifecycle()
        {
            var states = new Queue<HwInfoReaderOutcome>(
            [
                HwInfoReaderOutcome.Snapshot(Sensors,
                    [new HwInfoReadingEntry(0, 1, "CPU Package", "°C", 70)]),
                HwInfoReaderOutcome.SharedMemoryNotAvailable("mapping gone"),
                HwInfoReaderOutcome.Snapshot(Sensors,
                    [new HwInfoReadingEntry(0, 1, "CPU Package", "°C", 72)]),
            ]);
            var provider = new HwInfoTelemetryProvider(
                new StubReader(states.Dequeue),
                new StubProcessDetector(false));

            var first = await provider.ReadSnapshotAsync();
            var second = await provider.ReadSnapshotAsync();
            var third = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Ready, first.Status);
            Assert.Equal(TelemetrySourceStatus.Unavailable, second.Status); // 进程未运行 → Unavailable
            Assert.Equal(TelemetrySourceStatus.Ready, third.Status);       // 下次读取自动恢复
        }

        [Fact]
        public async Task Cancellation_IsRespected()
        {
            var provider = new HwInfoTelemetryProvider(
                new StubReader(() => throw new OperationCanceledException()),
                new StubProcessDetector(false));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.ReadSnapshotAsync(cts.Token));
        }
    }
}
