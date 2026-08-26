using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Aida64;

namespace AIGeekTuner.Tests.Services.Telemetry.Aida64
{
    public class Aida64TelemetryProviderTests
    {
        private sealed class StubReader(Aida64WmiQueryResult result) : IAida64WmiReader
        {
            public Aida64WmiQueryResult Query() => result;
        }

        private sealed class StubProcessDetector(bool running) : IAida64ProcessDetector
        {
            public bool IsRunning() => running;
        }

        private static Aida64WmiQueryResult Rows(params AigaRow[] rows) =>
            Aida64WmiQueryResult.Success(rows.Select(row =>
                new Aida64SensorRow(row.Id, row.Label, row.Value, row.Type)).ToArray());

        private sealed record AigaRow(
            string Id,
            string? Label = null,
            string? Value = null,
            string? Type = null);

        [Fact]
        public async Task ValidRows_ProduceReadyWithRawAndCanonical()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(
                    new AigaRow("TCPUPKG", "CPU Package", "72"),
                    new AigaRow("SCPUUTI", "CPU Utilization", "37"))),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Ready, result.Status);
            Assert.Equal(2, result.RawReadings.Count);
            Assert.Equal(2, result.CanonicalReadings.Count);
            Assert.All(result.RawReadings, reading =>
                Assert.Equal(TelemetrySourceKind.Aida64, reading.Source));
        }

        [Fact]
        public async Task MalformedValue_IsSkipped_OthersStillMapped()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(
                    new AigaRow("TCPUPKG", Value: "not-a-number"),
                    new AigaRow("SCPUUTI", Value: "44.5"))),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Ready, result.Status);
            var canonical = result.CanonicalReadings.Single();
            Assert.Equal(TelemetryMetricKey.CpuTotalUtilization, canonical.MetricKey);
            Assert.Contains("跳过异常 1 项", result.Message);
        }

        [Fact]
        public async Task MissingProperty_IsSkippedWithoutCrash()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(new AigaRow("TCPUPKG", Label: "CPU Package"))),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Ready, result.Status);
            Assert.Empty(result.CanonicalReadings);
            Assert.Empty(result.RawReadings);
        }

        [Fact]
        public async Task DuplicateId_FirstValueWins()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(
                    new AigaRow("SCPUUTI", Value: "30"),
                    new AigaRow("scpuuti", Value: "99"))),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Ready, result.Status);
            Assert.Equal(2, result.RawReadings.Count); // Raw 层保留全部真实行
            var utilization = Assert.Single(
                result.CanonicalReadings,
                reading => reading.MetricKey == TelemetryMetricKey.CpuTotalUtilization);
            Assert.Equal(30, utilization.Value);
        }

        [Fact]
        public async Task ClassMissing_NoProcess_ReturnsUnavailable()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Aida64WmiQueryResult.Failure(Aida64WmiFailureKind.ClassMissing)),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Unavailable, result.Status);
            Assert.Empty(result.CanonicalReadings);
        }

        [Fact]
        public async Task ClassMissing_ProcessRunning_ReturnsNeedsConfiguration()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Aida64WmiQueryResult.Failure(Aida64WmiFailureKind.ClassMissing)),
                new StubProcessDetector(true));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.NeedsConfiguration, result.Status);
            Assert.Contains("External Applications", result.Message);
        }

        [Fact]
        public async Task QueryFailed_ReturnsErrorWithoutThrowing()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Aida64WmiQueryResult.Failure(
                    Aida64WmiFailureKind.QueryFailed,
                    "COM failure (fake)")),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Equal(TelemetrySourceStatus.Error, result.Status);
        }

        [Fact]
        public async Task MultipleDevices_KeepDistinctRawAndCanonicalEntries()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(
                    new AigaRow("TGPU1", Value: "60"),
                    new AigaRow("TGPU2", Value: "50"),
                    new AigaRow("THDD1", Value: "39"),
                    new AigaRow("THDD2", Value: "41"))),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            var deviceKeys = result.CanonicalReadings
                .Select(reading => reading.Device.DeviceKey)
                .OrderBy(key => key)
                .ToArray();
            Assert.Equal(
                ["gpu:0", "gpu:1", "storage:hdd:1", "storage:hdd:2"],
                deviceKeys);
        }

        [Fact]
        public async Task Cancellation_BeforeRead_ThrowsOperationCanceled()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(new AigaRow("TCPUPKG", Value: "70"))),
                new StubProcessDetector(false));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.ReadSnapshotAsync(cts.Token));
        }

        [Fact]
        public async Task UnitHandling_ClocksAreMegahertz_TemperaturesCelsius()
        {
            var provider = new Aida64TelemetryProvider(
                new StubReader(Rows(
                    new AigaRow("SCPUCLK", Value: "4500"),
                    new AigaRow("TCPUPKG", Value: "65"))),
                new StubProcessDetector(false));

            var result = await provider.ReadSnapshotAsync();

            Assert.Contains(result.CanonicalReadings, reading =>
                reading.MetricKey == TelemetryMetricKey.CpuClock
                && reading.Unit == TelemetryUnit.Megahertz
                && Math.Abs(reading.Value - 4500) < 0.001);
            Assert.Contains(result.CanonicalReadings, reading =>
                reading.MetricKey == TelemetryMetricKey.CpuPackageTemperature
                && reading.Unit == TelemetryUnit.Celsius);
        }
    }
}
