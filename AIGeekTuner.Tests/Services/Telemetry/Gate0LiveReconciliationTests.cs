using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Aida64;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>
    /// Gate 0 真机回归：使用本机实际传感器字符串，防止 reconciliation / 分类再次回归。
    /// </summary>
    public class Gate0LiveReconciliationTests
    {
        private const string HwInfoGpuName = "GPU [#1]: NVIDIA GeForce RTX 4080 Laptop";
        private const string LhmNvidia = "NVIDIA GeForce RTX 4080 Laptop GPU";
        private const string LhmIntel = "Intel(R) UHD Graphics";
        private const string LhmCpu = "13th Gen Intel Core i9-13980HX";
        private const string HwInfoCpuName = "CPU [#0]: Intel Core i9-13980HX";

        [Fact]
        public void RealMachine_GpuAndCpu_ReconcileAsExpected()
        {
            var devices = new[]
            {
                // HWiNFO：GPU 一块 + CPU 变体传感器折叠后为单一 cpu 设备
                new SourceDeviceInfo(TelemetrySourceKind.HwInfo, TelemetryDeviceKind.Gpu, "gpu:13", HwInfoGpuName, 0, []),
                new SourceDeviceInfo(TelemetrySourceKind.HwInfo, TelemetryDeviceKind.Cpu, "cpu", HwInfoCpuName, 0, []),
                new SourceDeviceInfo(TelemetrySourceKind.LibreHardwareMonitor, TelemetryDeviceKind.Gpu, "gpu:0", LhmNvidia, 0, []),
                new SourceDeviceInfo(TelemetrySourceKind.LibreHardwareMonitor, TelemetryDeviceKind.Gpu, "gpu:1", LhmIntel, 1, []),
                new SourceDeviceInfo(TelemetrySourceKind.LibreHardwareMonitor, TelemetryDeviceKind.Cpu, "cpu", LhmCpu, 0, []),
                // AIDA：两块 GPU（dGPU+iGPU），CPU 单设备；名称均为占位/匿名
                new SourceDeviceInfo(TelemetrySourceKind.Aida64, TelemetryDeviceKind.Gpu, "gpu:0", "GPU #1", 0, []),
                new SourceDeviceInfo(TelemetrySourceKind.Aida64, TelemetryDeviceKind.Gpu, "gpu:1", "GPU #2", 1, []),
                new SourceDeviceInfo(TelemetrySourceKind.Aida64, TelemetryDeviceKind.Cpu, "cpu", "CPU", 0, []),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);
            var byMember = new Dictionary<(TelemetrySourceKind, string), string>();
            foreach (var group in groups)
            {
                foreach (var member in group.Members)
                {
                    byMember[(member.Source, member.NativeDeviceId)] = group.CanonicalKey;
                    Assert.False(group.CanonicalKey.StartsWith(
                        $"{group.Kind.ToString().ToLowerInvariant()}:{group.Kind.ToString().ToLowerInvariant()}:",
                        StringComparison.Ordinal),
                        $"duplicated kind prefix in {group.CanonicalKey}");
                }
            }

            // dGPU：HWiNFO + LHM 按包含关系合并；AIDA 两个匿名 GPU 无法定位 → 不挂靠。
            var nvidiaKey = byMember[(TelemetrySourceKind.HwInfo, "gpu:13")];
            Assert.Equal(nvidiaKey, byMember[(TelemetrySourceKind.LibreHardwareMonitor, "gpu:0")]);
            Assert.StartsWith("gpu:name:", nvidiaKey);

            Assert.StartsWith("src:Aida64:", byMember[(TelemetrySourceKind.Aida64, "gpu:0")]);
            Assert.StartsWith("src:Aida64:", byMember[(TelemetrySourceKind.Aida64, "gpu:1")]);
            Assert.NotEqual(byMember[(TelemetrySourceKind.Aida64, "gpu:0")],
                byMember[(TelemetrySourceKind.Aida64, "gpu:1")]);
            Assert.StartsWith("src:", byMember[(TelemetrySourceKind.LibreHardwareMonitor, "gpu:1")]);

            // CPU：三源各一（HWiNFO 已折叠变体）→ 无矛盾单例合并。
            var cpuKey = byMember[(TelemetrySourceKind.HwInfo, "cpu")];
            Assert.Equal("cpu:singleton", cpuKey);
            Assert.Equal(cpuKey, byMember[(TelemetrySourceKind.LibreHardwareMonitor, "cpu")]);
            Assert.Equal(cpuKey, byMember[(TelemetrySourceKind.Aida64, "cpu")]);
        }

        [Fact]
        public void Aida64_MemClockId_ClassifiesWithoutCrash_AndStaysRaw()
        {
            // 真机数据包含 SGPU1MEMCLK；旧实现因无捕获组抛 FormatException。
            var ok = Aida64CanonicalMapper.TryClassifyDeviceAndUnit("SGPU1MEMCLK", out var device, out var unit);

            Assert.True(ok);
            Assert.Equal("gpu:0", device.DeviceKey);
            Assert.Equal(TelemetryUnit.Megahertz, unit);
        }
    }
}
