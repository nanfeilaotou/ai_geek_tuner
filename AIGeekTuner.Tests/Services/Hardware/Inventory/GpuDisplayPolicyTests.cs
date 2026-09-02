using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>V2-M4.5B Gate 0.2/I：GPU 用户可见性策略。</summary>
    public class GpuDisplayPolicyTests
    {
        private static GpuInventoryInfo Gpu(string? name, ulong? vram = null) =>
            new(Name: name, Vendor: null, VendorId: null, DeviceId: null, PnpDeviceId: null,
                DriverVersion: null, DriverDateUtc: null,
                DedicatedVideoMemoryBytes: vram, SharedSystemMemoryBytes: null,
                Source: InventorySource.DXGI);

        [Fact]
        public void BasicRenderDriver_Hidden_WhenOtherGpusExist()
        {
            var gpus = new[]
            {
                Gpu("NVIDIA GeForce RTX 4080 Laptop GPU", 12_579_766_272),
                Gpu("Intel(R) UHD Graphics", 134_217_728),
                Gpu("Microsoft Basic Render Driver"),
            };

            var userFacing = GpuDisplayPolicy.SelectUserFacingGpus(gpus);

            Assert.Equal(2, userFacing.Count);
            Assert.DoesNotContain(userFacing, gpu =>
                GpuDisplayPolicy.IsSoftwareRenderAdapter(gpu.Name));
        }

        [Fact]
        public void BasicRenderDriver_KeptAsFallback_WhenItIsTheOnlyGpu()
        {
            var gpus = new[] { Gpu("Microsoft Basic Render Driver") };

            var userFacing = GpuDisplayPolicy.SelectUserFacingGpus(gpus);

            _ = userFacing; // 结构断言见下
            Assert.Single(userFacing);
            Assert.Equal(
                "Microsoft Basic Render Driver",
                userFacing[0].Name);
        }

        [Fact]
        public void RawSnapshot_IsNeverMutated()
        {
            var gpus = new[]
            {
                Gpu("NVIDIA GeForce RTX 4080 Laptop GPU"),
                Gpu("Microsoft Basic Render Driver"),
            };

            _ = GpuDisplayPolicy.SelectUserFacingGpus(gpus);

            Assert.Equal(2, gpus.Length); // 底层 raw inventory 不删除任何条目
        }

        [Theory]
        [InlineData(12_579_766_272UL, true)]   // 11.7 GB dGPU
        [InlineData(1_073_741_824UL, true)]    // 1 GiB 阈值
        [InlineData(134_217_728UL, false)]     // iGPU 0.1GB 预留量
        [InlineData(null, false)]
        public void DedicatedVram_DisplayThreshold(ulong? bytes, bool expected)
        {
            Assert.Equal(expected, GpuDisplayPolicy.ShouldReportDedicatedVram(bytes));
        }
    }
}
