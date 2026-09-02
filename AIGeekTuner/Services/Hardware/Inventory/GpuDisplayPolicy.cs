using System;
using System.Collections.Generic;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5B Gate 0.2：GPU 用户可见性策略（纯展示层规则，不改 raw snapshot）。
    ///
    /// 1) Microsoft Basic Render Driver 是系统软件渲染兜底，不得出现在
    ///    Dashboard GPU 行 / Hardware 普通 GPU 卡片；只有当系统没有任何其它
    ///    有效 GPU 时才允许作为 fallback 诊断信息出现。
    /// 2) iGPU 常报告几十 MB 的 DedicatedVideoMemory（如 0.1GB），直接展示会
    ///    误导用户；低于阈值时不显示 Dedicated VRAM 字段（共享内存照常）。
    /// </summary>
    public static class GpuDisplayPolicy
    {
        /// <summary>Windows 软件渲染兜底适配器的官方名称（WMI/DXGI Name）。</summary>
        public const string SoftwareRenderAdapterName = "Microsoft Basic Render Driver";

        /// <summary>Dedicated VRAM 展示阈值：低于 1 GiB 视为 iGPU 预留量，不展示。</summary>
        public const ulong DedicatedVramDisplayThresholdBytes = 1024UL * 1024UL * 1024UL;

        public static bool IsSoftwareRenderAdapter(string? name) =>
            string.Equals(
                name?.Trim(),
                SoftwareRenderAdapterName,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 用户可见 GPU 列表：剔除 Basic Render Driver；当剔除后为空
        /// （系统真的只有软件渲染兜底）时保留它作为 fallback 诊断信息。
        /// </summary>
        public static IReadOnlyList<GpuInventoryInfo> SelectUserFacingGpus(
            IReadOnlyList<GpuInventoryInfo> gpus)
        {
            ArgumentNullException.ThrowIfNull(gpus);

            var real = new List<GpuInventoryInfo>();
            var software = new List<GpuInventoryInfo>();
            foreach (var gpu in gpus)
            {
                if (IsSoftwareRenderAdapter(gpu.Name))
                {
                    software.Add(gpu);
                }
                else
                {
                    real.Add(gpu);
                }
            }

            return real.Count > 0 ? real : software;
        }

        /// <summary>Dedicated VRAM 是否值得展示（iGPU 小预留量不展示）。</summary>
        public static bool ShouldReportDedicatedVram(ulong? dedicatedVideoMemoryBytes) =>
            dedicatedVideoMemoryBytes is >= DedicatedVramDisplayThresholdBytes;
    }
}
