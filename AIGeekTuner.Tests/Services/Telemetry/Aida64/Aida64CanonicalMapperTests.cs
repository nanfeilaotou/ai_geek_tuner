using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Aida64;

namespace AIGeekTuner.Tests.Services.Telemetry.Aida64
{
    public class Aida64CanonicalMapperTests
    {
        private static readonly DateTimeOffset CapturedAtUtc =
            new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

        private static RawTelemetryReading Raw(
            string id,
            double value,
            TelemetryUnit unit,
            TelemetryDeviceIdentity? device = null,
            string? label = null)
        {
            var resolvedDevice = device ?? TelemetryDeviceIdentity.SystemBoard("system");
            var info = new SourceDeviceInfo(
                TelemetrySourceKind.Aida64,
                resolvedDevice.Kind,
                resolvedDevice.DeviceKey,
                resolvedDevice.DisplayName,
                0,
                []);
            return new(
                TelemetrySourceKind.Aida64,
                id,
                label ?? id,
                value,
                unit,
                resolvedDevice,
                info,
                CapturedAtUtc);
        }

        [Fact]
        public void CpuTemperature_PrefersPackageOverTctlAndGeneric()
        {
            var cpu = TelemetryDeviceIdentity.Cpu("CPU");
            var raw = new[]
            {
                Raw("TCPU", 70, TelemetryUnit.Celsius, cpu),
                Raw("TCPUTCTL", 71, TelemetryUnit.Celsius, cpu),
                Raw("TCPUPKG", 72, TelemetryUnit.Celsius, cpu),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            var reading = Assert.Single(canonical);
            Assert.Equal(TelemetryMetricKey.CpuPackageTemperature, reading.MetricKey);
            Assert.Equal(72, reading.Value);
            Assert.Equal(TelemetryUnit.Celsius, reading.Unit);
        }

        [Fact]
        public void CpuSystemMetrics_MapUtilizationThrottlingAndClock()
        {
            var cpu = TelemetryDeviceIdentity.Cpu("CPU");
            var raw = new[]
            {
                Raw("SCPUUTI", 43, TelemetryUnit.Percent, cpu),
                Raw("SCPUTHR", 0, TelemetryUnit.Percent, cpu),
                Raw("SCPUCLK", 3800, TelemetryUnit.Megahertz, cpu),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            Assert.Contains(canonical, r =>
                r.MetricKey == TelemetryMetricKey.CpuTotalUtilization && r.Value == 43);
            Assert.Contains(canonical, r =>
                r.MetricKey == TelemetryMetricKey.CpuThrottling && r.Value == 0);
            Assert.Contains(canonical, r =>
                r.MetricKey == TelemetryMetricKey.CpuClock && r.Value == 3800);
        }

        [Fact]
        public void GpuFamily_MapsCoreHotspotMemoryPerDevice()
        {
            var gpu1 = TelemetryDeviceIdentity.GpuByIndex(0, "GPU #1");
            var raw = new[]
            {
                Raw("TGPU1", 68, TelemetryUnit.Celsius, gpu1),
                Raw("TGPU1HOT", 76, TelemetryUnit.Celsius, gpu1),
                Raw("TGPU1MEM", 64, TelemetryUnit.Celsius, gpu1),
                Raw("SGPU1CLK", 2100, TelemetryUnit.Megahertz, gpu1),
                Raw("SGPU1UTI", 97, TelemetryUnit.Percent, gpu1),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            Assert.Equal(5, canonical.Count);
            Assert.All(canonical, r => Assert.Equal("gpu:0", r.Device.DeviceKey));
            Assert.Contains(canonical, r =>
                r.MetricKey == TelemetryMetricKey.GpuHotspotTemperature && r.Value == 76);
            Assert.Contains(canonical, r =>
                r.MetricKey == TelemetryMetricKey.GpuMemoryTemperature && r.Value == 64);
        }

        [Fact]
        public void MultipleGpus_StaySeparated()
        {
            var gpu1 = TelemetryDeviceIdentity.GpuByIndex(0, "GPU #1");
            var gpu2 = TelemetryDeviceIdentity.GpuByIndex(1, "GPU #2");
            var raw = new[]
            {
                Raw("TGPU1", 68, TelemetryUnit.Celsius, gpu1),
                Raw("TGPU2", 55, TelemetryUnit.Celsius, gpu2),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            var byKey = canonical.ToDictionary(r => r.Device.DeviceKey, r => r.Value);
            Assert.Equal(2, byKey.Count);
            Assert.Equal(68, byKey["gpu:0"]);
            Assert.Equal(55, byKey["gpu:1"]);
        }

        [Fact]
        public void MemoryUsed_AndGpuDedicatedMemory_ConvertMbToBytes()
        {
            var memory = TelemetryDeviceIdentity.Memory("Memory");
            var gpu1 = TelemetryDeviceIdentity.GpuByIndex(0, "GPU #1");
            var raw = new[]
            {
                Raw("SUSEDMEM", 8192, TelemetryUnit.Megabyte, memory),
                Raw("SMEMUTI", 51, TelemetryUnit.Percent, memory),
                Raw("SGPU1USEDDEMEM", 2048, TelemetryUnit.Megabyte, gpu1),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            Assert.Equal(8192 * 1048576d,
                canonical.Single(r => r.MetricKey == TelemetryMetricKey.MemoryUsed).Value);
            Assert.Equal(2048 * 1048576d,
                canonical.Single(r => r.MetricKey == TelemetryMetricKey.GpuMemoryUsed).Value);
        }

        [Fact]
        public void StorageTemperature_MapsOfficialHddIndexToDevice()
        {
            var raw = new[] { Raw("THDD3", 41, TelemetryUnit.Celsius) };

            // 分类器负责把 THDDn 挂到对应存储设备；这里先分类再映射，模拟 Provider 流程。
            Assert.True(Aida64CanonicalMapper.TryClassifyDeviceAndUnit(
                "THDD3", out var device, out var unit));
            Assert.Equal(TelemetryUnit.Celsius, unit);

            var withDevice = new[] { Raw("THDD3", 41, TelemetryUnit.Celsius, device) };
            var canonical = Aida64CanonicalMapper.Map(withDevice);

            var reading = Assert.Single(canonical);
            Assert.Equal(TelemetryMetricKey.StorageTemperature, reading.MetricKey);
            Assert.Equal("storage:hdd:3", reading.Device.DeviceKey);
            Assert.Equal(41, reading.Value);
        }

        [Fact]
        public void UnknownIds_NeverEnterCanonical()
        {
            var raw = new[]
            {
                Raw("SREGVALS1", 1, TelemetryUnit.None),
                Raw("SBATTLVL", 88, TelemetryUnit.Percent),
                Raw("TCPU", -999, TelemetryUnit.Celsius), // 物理不合理
                Raw("SCPUUTI", 250, TelemetryUnit.Percent), // 超出百分比范围
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            Assert.Empty(canonical);
        }
    }
}
