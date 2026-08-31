using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>
    /// V2-M3.3 显示策略回归：普通页面绝不出现匿名占位设备；
    /// 绝不因减少重复而猜测归属（未合并的 AIDA GPU 保持隐藏）。
    /// </summary>
    public sealed class HardwareDisplayPolicyTests
    {
        [Theory]
        [InlineData("gpu:1", "GPU #1")]
        [InlineData("gpu:2", "GPU #2")]
        [InlineData("src:Aida64:gpu:0", "GPU #1")]
        public void AnonymousGpu_IsNotUserVisible(string deviceKey, string displayName)
        {
            // §3：匿名 GPU 无任何回退——普通页面绝不出现 GPU #n。
            var gpu = new TelemetryDeviceIdentity(TelemetryDeviceKind.Gpu, deviceKey, displayName);

            Assert.False(HardwareDisplayPolicy.IsUserVisible(gpu));
        }

        [Fact]
        public void NamedUnresolvedSingleSourceGpu_IsUserVisible_CaseB()
        {
            // 来源给出真实型号名的未合并单源设备：允许显示（规则 B）。
            var aidaNamed = new TelemetryDeviceIdentity(
                TelemetryDeviceKind.Gpu, "src:Aida64:gpu:0", "Intel UHD Graphics");

            Assert.True(HardwareDisplayPolicy.IsUserVisible(aidaNamed));
        }

        [Fact]
        public void ResolvedCluster_RealModelName_IsUserVisible()
        {
            var nvidia = new TelemetryDeviceIdentity(
                TelemetryDeviceKind.Gpu, "gpu:name:nvidia-geforce-rtx-4080-laptop-gpu",
                "NVIDIA GeForce RTX 4080 Laptop GPU");

            Assert.True(HardwareDisplayPolicy.IsUserVisible(nvidia));
        }

        [Theory]
        [InlineData(TelemetryDeviceKind.Cpu)]
        [InlineData(TelemetryDeviceKind.Memory)]
        public void SingleLogicalEntities_AreAlwaysUserVisible(TelemetryDeviceKind kind)
        {
            var generic = new TelemetryDeviceIdentity(kind, kind.ToString(), kind.ToString());

            Assert.True(HardwareDisplayPolicy.IsUserVisible(generic));
        }

        [Fact]
        public void HwInfoPrefixedRealName_IsNormalizedVisible()
        {
            // HWiNFO “GPU [#0]: 前缀” 归一后仍有真实型号。
            var hwinfoGpu = new TelemetryDeviceIdentity(
                TelemetryDeviceKind.Gpu, "gpu:0",
                "GPU [#0]: NVIDIA GeForce RTX 4080 Laptop GPU");

            Assert.True(HardwareDisplayPolicy.IsUserVisible(hwinfoGpu));
        }
    }
}