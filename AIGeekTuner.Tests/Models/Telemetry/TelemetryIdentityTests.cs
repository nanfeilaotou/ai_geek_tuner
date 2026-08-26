using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Tests.Models.Telemetry
{
    public class TelemetryIdentityTests
    {
        [Fact]
        public void MetricKey_EqualValue_IsEqual()
        {
            var a = new TelemetryMetricKey("cpu.package.temperature");
            Assert.Equal(TelemetryMetricKey.CpuPackageTemperature, a);
            Assert.Equal(a.GetHashCode(), TelemetryMetricKey.CpuPackageTemperature.GetHashCode());
        }

        [Fact]
        public void TwoGpus_NeverShareDeviceKey()
        {
            var gpu0 = TelemetryDeviceIdentity.GpuByIndex(0, "NVIDIA RTX 4080 Laptop GPU");
            var gpu1 = TelemetryDeviceIdentity.GpuByIndex(1, "AMD Radeon 780M");

            Assert.NotEqual(gpu0.DeviceKey, gpu1.DeviceKey);
            Assert.Equal(TelemetryDeviceKind.Gpu, gpu0.Kind);
            Assert.Equal(TelemetryDeviceKind.Gpu, gpu1.Kind);
        }

        [Fact]
        public void StorageKeys_DeriveFromStableIdentifier_NotDisplayNameOnly()
        {
            var a = TelemetryDeviceIdentity.Storage("Samsung SSD 990 PRO", "Samsung SSD 990 PRO");
            var b = TelemetryDeviceIdentity.Storage("Samsung SSD 990 PRO", "Samsung SSD 990 PRO");

            // 同一稳定标识得到同一 key —— 显示名相同但标识不同时不会合并。
            var c = TelemetryDeviceIdentity.Storage("Samsung SSD 990 PRO#2", "Samsung SSD 990 PRO");
            Assert.Equal(a.DeviceKey, b.DeviceKey);
            Assert.NotEqual(a.DeviceKey, c.DeviceKey);
        }

        [Fact]
        public void DeviceKinds_AreDistinctAcrossCategories()
        {
            var cpu = TelemetryDeviceIdentity.Cpu("CPU");
            var mem = TelemetryDeviceIdentity.Memory("Memory");

            Assert.NotEqual(cpu.DeviceKey, mem.DeviceKey);
        }
    }
}
