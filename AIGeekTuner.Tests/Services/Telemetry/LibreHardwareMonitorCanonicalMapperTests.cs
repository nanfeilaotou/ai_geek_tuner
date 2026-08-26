using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.LibreHardwareMonitor;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    public class LibreHardwareMonitorCanonicalMapperTests
    {
        private static readonly DateTimeOffset CapturedAtUtc =
            new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static RawTelemetryReading Raw(
            TelemetryDeviceIdentity device,
            TelemetryUnit unit,
            string label,
            double value,
            string? id = null)
        {
            var info = new SourceDeviceInfo(
                TelemetrySourceKind.LibreHardwareMonitor,
                device.Kind,
                device.DeviceKey,
                device.DisplayName,
                0,
                []);
            return new(
                TelemetrySourceKind.LibreHardwareMonitor,
                id ?? $"label:{label}",
                label,
                value,
                unit,
                device,
                info,
                CapturedAtUtc);
        }

        private static readonly TelemetryDeviceIdentity Cpu =
            TelemetryDeviceIdentity.Cpu("Test CPU");

        [Fact]
        public void CpuPackageTemperature_PrefersPackageOverCoreSensors()
        {
            var raw = new[]
            {
                Raw(Cpu, TelemetryUnit.Celsius, "Core #1", 61),
                Raw(Cpu, TelemetryUnit.Celsius, "Core Max", 70),
                Raw(Cpu, TelemetryUnit.Celsius, "Package", 72),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            var reading = Assert.Single(
                canonical,
                reading => reading.MetricKey == TelemetryMetricKey.CpuPackageTemperature);
            Assert.Equal(72, reading.Value);
            Assert.Equal(TelemetryUnit.Celsius, reading.Unit);
            Assert.Equal("Package", reading.SourceLabel);
        }

        [Fact]
        public void CpuMetrics_MapUtilizationClockAndPower()
        {
            var raw = new[]
            {
                Raw(Cpu, TelemetryUnit.Percent, "CPU Total", 37),
                Raw(Cpu, TelemetryUnit.Megahertz, "Core Average", 4200),
                Raw(Cpu, TelemetryUnit.Watt, "CPU Package", 88.5),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            Assert.Contains(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.CpuTotalUtilization
                && reading.Value == 37);
            Assert.Contains(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.CpuClock && reading.Value == 4200);
            Assert.Contains(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.CpuPackagePower
                && reading.Value == 88.5);
        }

        [Fact]
        public void TwoGpus_MappedSeparately_NoValueMixing()
        {
            var gpu0 = TelemetryDeviceIdentity.GpuByIndex(0, "dGPU");
            var gpu1 = TelemetryDeviceIdentity.GpuByIndex(1, "iGPU");
            var raw = new[]
            {
                Raw(gpu0, TelemetryUnit.Celsius, "GPU Core", 68),
                Raw(gpu0, TelemetryUnit.Percent, "GPU Core", 96),
                Raw(gpu1, TelemetryUnit.Celsius, "GPU Core", 52),
                Raw(gpu1, TelemetryUnit.Percent, "GPU Core", 11),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            var temperatures = canonical
                .Where(reading => reading.MetricKey == TelemetryMetricKey.GpuCoreTemperature)
                .ToDictionary(reading => reading.Device.DeviceKey, reading => reading.Value);
            var utilizations = canonical
                .Where(reading => reading.MetricKey == TelemetryMetricKey.GpuCoreUtilization)
                .ToDictionary(reading => reading.Device.DeviceKey, reading => reading.Value);

            Assert.Equal(2, temperatures.Count);
            Assert.Equal(68, temperatures["gpu:0"]);
            Assert.Equal(52, temperatures["gpu:1"]);
            Assert.Equal(96, utilizations["gpu:0"]);
            Assert.Equal(11, utilizations["gpu:1"]);
        }

        [Fact]
        public void StorageTemperatures_PerDeviceMax()
        {
            var ssdA = TelemetryDeviceIdentity.Storage("SSD A", "SSD A");
            var ssdB = TelemetryDeviceIdentity.Storage("SSD B", "SSD B");
            var raw = new[]
            {
                Raw(ssdA, TelemetryUnit.Celsius, "Temperature", 41),
                Raw(ssdA, TelemetryUnit.Celsius, "Composite", 44),
                Raw(ssdB, TelemetryUnit.Celsius, "Temperature", 38),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            var byKey = canonical
                .Where(reading => reading.MetricKey == TelemetryMetricKey.StorageTemperature)
                .ToDictionary(reading => reading.Device.DeviceKey, reading => reading.Value);
            Assert.Equal(2, byKey.Count);
            Assert.Equal(44, byKey["storage:SSD A"]);
            Assert.Equal(38, byKey["storage:SSD B"]);
        }

        [Fact]
        public void UnknownSensors_RemainRawOnly_AndAreNeverMapped()
        {
            var raw = new[]
            {
                Raw(Cpu, TelemetryUnit.None, "Noise", 31), // 无法归类的原生读数
                Raw(Cpu, TelemetryUnit.Volt, "Vcore", 1.21), // 电压不在本轮核心指标集
                Raw(Cpu, TelemetryUnit.Celsius, "Temperature #3", 999), // 超出物理合理上限
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            Assert.DoesNotContain(canonical, reading =>
                reading.SourceLabel is "Noise" or "Vcore" or "Temperature #3");
            Assert.DoesNotContain(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.CpuPackageTemperature);
        }

        [Fact]
        public void MemoryUsed_ConvertsGbToBytes()
        {
            var memory = TelemetryDeviceIdentity.Memory("Physical Memory");
            var raw = new[]
            {
                Raw(memory, TelemetryUnit.Gigabyte, "Used Memory", 16),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            var reading = Assert.Single(
                canonical,
                item => item.MetricKey == TelemetryMetricKey.MemoryUsed);
            Assert.Equal(TelemetryUnit.Byte, reading.Unit);
            Assert.Equal(16 * 1073741824d, reading.Value);
        }

        [Fact]
        public void Readings_PreserveSourceProvenance()
        {
            var raw = new[]
            {
                Raw(Cpu, TelemetryUnit.Watt, "CPU Package", 42, id: "Power:CPU Package"),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            var reading = Assert.Single(canonical);
            Assert.Equal(TelemetrySourceKind.LibreHardwareMonitor, reading.Source);
            Assert.Equal("Power:CPU Package", reading.SourceMetricId);
            Assert.Equal("CPU Package", reading.SourceLabel);
            Assert.Equal(CapturedAtUtc, reading.CapturedAtUtc);
        }
    }
}
