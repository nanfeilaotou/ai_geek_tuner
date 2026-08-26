using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.HwInfo;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>§29 TelemetryHub fallback 场景 A–F + 隔离/超时/溯源。</summary>
    public class TelemetryHubFallbackTests
    {
        private static readonly DateTimeOffset CapturedAtUtc =
            new(2024, 9, 1, 8, 0, 0, TimeSpan.Zero);

        private sealed class StubProvider(
            TelemetrySourceKind kind,
            Func<CancellationToken, Task<TelemetryProviderResult>> factory) : ITelemetryProvider
        {
            public TelemetrySourceKind SourceKind => kind;

            public Task<TelemetryProviderResult> ReadSnapshotAsync(
                CancellationToken cancellationToken = default) =>
                factory(cancellationToken);
        }

        private static ITelemetryProvider ReadyWith(
            TelemetrySourceKind kind,
            params (TelemetryDeviceIdentity Device, TelemetryMetricKey Metric, double Value)[] readings) =>
            new StubProvider(kind, _ => Task.FromResult(new TelemetryProviderResult(
                TelemetrySourceStatus.Ready,
                "ready",
                readings.Select(entry => new RawTelemetryReading(
                    kind,
                    $"raw:{entry.Metric.Value}",
                    entry.Metric.Value,
                    entry.Value,
                    TelemetryUnit.None,
                    entry.Device,
                    new SourceDeviceInfo(
                        kind,
                        entry.Device.Kind,
                        entry.Device.DeviceKey,
                        entry.Device.DisplayName,
                        0,
                        []),
                    CapturedAtUtc)).ToArray(),
                readings.Select(entry => new TelemetryReading(
                    entry.Metric,
                    entry.Value,
                    UnitOf(entry.Metric),
                    entry.Device,
                    kind,
                    $"raw:{entry.Metric.Value}",
                    entry.Metric.Value,
                    CapturedAtUtc)).ToArray(),
                CapturedAtUtc)));

        private static TelemetryUnit UnitOf(TelemetryMetricKey metric) =>
            metric == TelemetryMetricKey.CpuPackageTemperature
                ? TelemetryUnit.Celsius
                : TelemetryUnit.Percent;

        private static ITelemetryProvider StatusOnly(TelemetrySourceKind kind, TelemetrySourceStatus status) =>
            new StubProvider(kind, _ => Task.FromResult(
                TelemetryProviderResult.Empty(kind, status, status.ToString(), DateTimeOffset.UtcNow)));

        private static ITelemetryProvider Throwing(TelemetrySourceKind kind) =>
            new StubProvider(kind, _ => throw new InvalidOperationException($"{kind} exploded"));

        private static readonly TelemetryDeviceIdentity Cpu = TelemetryDeviceIdentity.Cpu("CPU");

        [Fact]
        public async Task ScenarioA_HwInfoPresent_WinsOverOthers()
        {
            var hub = new TelemetryHub(
            [
                ReadyWith(TelemetrySourceKind.HwInfo,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 71.4)),
                ReadyWith(TelemetrySourceKind.Aida64,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 72)),
                ReadyWith(TelemetrySourceKind.LibreHardwareMonitor,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 73)),
            ]);

            var snapshot = await hub.ReadAsync();

            var cpuTemp = Assert.Single(snapshot.CanonicalReadings);
            Assert.Equal(71.4, cpuTemp.Value);
            Assert.Equal(TelemetrySourceKind.HwInfo, cpuTemp.Source);
        }

        [Fact]
        public async Task ScenarioB_HwInfoMissingMetric_FallsBackToAida()
        {
            var hub = new TelemetryHub(
            [
                ReadyWith(TelemetrySourceKind.HwInfo), // Ready 但没有 CPU 温度
                ReadyWith(TelemetrySourceKind.Aida64,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 72)),
                ReadyWith(TelemetrySourceKind.LibreHardwareMonitor,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 73)),
            ]);

            var snapshot = await hub.ReadAsync();

            var cpuTemp = Assert.Single(snapshot.CanonicalReadings);
            Assert.Equal(TelemetrySourceKind.Aida64, cpuTemp.Source);
        }

        [Fact]
        public async Task ScenarioC_BothExternalUnavailable_LhmServes()
        {
            var hub = new TelemetryHub(
            [
                StatusOnly(TelemetrySourceKind.HwInfo, TelemetrySourceStatus.Unavailable),
                StatusOnly(TelemetrySourceKind.Aida64, TelemetrySourceStatus.Unavailable),
                ReadyWith(TelemetrySourceKind.LibreHardwareMonitor,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 73)),
            ]);

            var snapshot = await hub.ReadAsync();

            var cpuTemp = Assert.Single(snapshot.CanonicalReadings);
            Assert.Equal(TelemetrySourceKind.LibreHardwareMonitor, cpuTemp.Source);
        }

        [Fact]
        public async Task ScenarioD_HwInfoThrows_HubStillReturnsAidaAndLhm()
        {
            var hub = new TelemetryHub(
            [
                Throwing(TelemetrySourceKind.HwInfo),
                ReadyWith(TelemetrySourceKind.Aida64,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 72)),
                ReadyWith(TelemetrySourceKind.LibreHardwareMonitor,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 73)),
            ]);

            var snapshot = await hub.ReadAsync();

            Assert.Equal(TelemetrySourceStatus.Error,
                SingleReport(snapshot, TelemetrySourceKind.HwInfo).Status);
            Assert.Contains(snapshot.CanonicalReadings, reading =>
                reading.Source == TelemetrySourceKind.Aida64);
            // LHM 的同指标被优先级屏蔽，但来源仍在报告中。
            Assert.Single(snapshot.CanonicalReadings);
        }

        [Fact]
        public async Task ScenarioE_AllSourcesUnavailable_ReturnsDegradedEmptySnapshot()
        {
            var hub = new TelemetryHub(
            [
                StatusOnly(TelemetrySourceKind.HwInfo, TelemetrySourceStatus.Unavailable),
                StatusOnly(TelemetrySourceKind.Aida64, TelemetrySourceStatus.NeedsConfiguration),
                StatusOnly(TelemetrySourceKind.LibreHardwareMonitor, TelemetrySourceStatus.Unavailable),
            ]);

            var snapshot = await hub.ReadAsync(); // 绝不抛出

            Assert.Empty(snapshot.CanonicalReadings);
            Assert.Equal(3, snapshot.Sources.Count);
        }

        [Fact]
        public async Task ScenarioF_SameSourceMultipleGpus_NoValueMixing()
        {
            var gpu0 = TelemetryDeviceIdentity.GpuByIndex(0, "dGPU");
            var gpu1 = TelemetryDeviceIdentity.GpuByIndex(1, "iGPU");
            var hub = new TelemetryHub(
            [
                ReadyWith(TelemetrySourceKind.LibreHardwareMonitor,
                    (gpu0, TelemetryMetricKey.CpuPackageTemperature, 68),
                    (gpu1, TelemetryMetricKey.CpuPackageTemperature, 52)),
            ]);
            // 注：这里借用 CpuPackageTemperature 键名只为构造两条同指标不同设备的读数。

            var snapshot = await hub.ReadAsync();

            Assert.Equal(2, snapshot.CanonicalReadings.Count);
            Assert.Equal([68, 52], snapshot.CanonicalReadings.Select(reading => reading.Value).ToArray());
        }

        [Fact]
        public async Task SlowProvider_TimesOut_OthersStillServed()
        {
            var hanging = new StubProvider(TelemetrySourceKind.HwInfo, async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return TelemetryProviderResult.Empty(
                    TelemetrySourceKind.HwInfo, TelemetrySourceStatus.Ready, "", DateTimeOffset.UtcNow);
            });
            var hub = new TelemetryHub(
                [hanging, ReadyWith(TelemetrySourceKind.Aida64,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 72))],
                perProviderTimeout: TimeSpan.FromMilliseconds(120));

            var snapshot = await hub.ReadAsync();

            Assert.Equal(TelemetrySourceStatus.Error,
                SingleReport(snapshot, TelemetrySourceKind.HwInfo).Status);
            Assert.Single(snapshot.CanonicalReadings);
        }

        [Fact]
        public async Task Provenance_PreservedThroughSelection()
        {
            var hub = new TelemetryHub(
            [
                ReadyWith(TelemetrySourceKind.Aida64,
                    (Cpu, TelemetryMetricKey.CpuPackageTemperature, 72)),
            ]);

            var snapshot = await hub.ReadAsync();

            var reading = Assert.Single(snapshot.CanonicalReadings);
            Assert.Equal("raw:cpu.package.temperature", reading.SourceMetricId);
            Assert.Equal(CapturedAtUtc, reading.CapturedAtUtc);
        }

        [Fact]
        public void Constructor_DuplicateKind_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => new TelemetryHub(
            [
                StatusOnly(TelemetrySourceKind.Aida64, TelemetrySourceStatus.Ready),
                StatusOnly(TelemetrySourceKind.Aida64, TelemetrySourceStatus.Ready),
            ]));
        }

        [Fact]
        public async Task Reports_CoverEveryRegisteredSource_InPriorityOrder()
        {
            var hub = new TelemetryHub(
            [
                StatusOnly(TelemetrySourceKind.LibreHardwareMonitor, TelemetrySourceStatus.Ready),
                Throwing(TelemetrySourceKind.HwInfo),
                ReadyWith(TelemetrySourceKind.Aida64,
                    (Cpu, TelemetryMetricKey.CpuTotalUtilization, 40)),
            ]);

            var snapshot = await hub.ReadAsync();

            Assert.Equal(
                [TelemetrySourceKind.HwInfo, TelemetrySourceKind.Aida64, TelemetrySourceKind.LibreHardwareMonitor],
                snapshot.Sources.Select(report => report.Source).ToArray());
        }

        private static TelemetrySourceReport SingleReport(
            TelemetrySnapshot snapshot,
            TelemetrySourceKind kind) =>
            Assert.Single(snapshot.Sources, report => report.Source == kind);
    }
}
