using System;
using System.Collections.Generic;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate F：GPU inventory。主数据 = DXGI DedicatedVideoMemory（避免
    /// Win32_VideoController.AdapterRAM 的 32 位截断，12GB RTX 4080 必须正确）；
    /// Win32_VideoController 补充 driver version/date 与 PNPDeviceID；
    /// DXGI 缺失时退化为纯 WMI 条目（VRAM 不可信则留 null，不显示错误数值）。
    /// 两个 GPU（iGPU + dGPU）各自独立。
    /// </summary>
    public static class GpuInventoryMapper
    {
        public sealed record AdapterDescriptor(
            string Description,
            ushort VendorId,
            ushort DeviceId,
            ulong DedicatedVideoMemoryBytes,
            ulong SharedSystemMemoryBytes);

        public static IReadOnlyList<GpuInventoryInfo> Map(
            IReadOnlyList<AdapterDescriptor> dxgiAdapters,
            IReadOnlyList<IInventoryRow> videoControllers)
        {
            var results = new List<GpuInventoryInfo>(Math.Max(dxgiAdapters.Count, 1));
            var usedControllerIndexes = new HashSet<int>();

            foreach (var adapter in dxgiAdapters)
            {
                var controllerIndex = FindController(videoControllers, adapter.Description, usedControllerIndexes);
                if (controllerIndex >= 0)
                {
                    usedControllerIndexes.Add(controllerIndex);
                }

                var controller = controllerIndex >= 0 ? videoControllers[controllerIndex] : null;
                results.Add(new GpuInventoryInfo(
                    Name: adapter.Description,
                    Vendor: controller?.String("AdapterCompatibility") ?? VendorIdToName(adapter.VendorId),
                    VendorId: adapter.VendorId,
                    DeviceId: adapter.DeviceId,
                    PnpDeviceId: controller?.String("PNPDeviceID"),
                    DriverVersion: controller?.String("DriverVersion"),
                    DriverDateUtc: ParseDriverDate(controller?.String("DriverDate")),
                    DedicatedVideoMemoryBytes:
                        adapter.DedicatedVideoMemoryBytes > 0 ? adapter.DedicatedVideoMemoryBytes : null,
                    SharedSystemMemoryBytes:
                        adapter.SharedSystemMemoryBytes > 0 ? adapter.SharedSystemMemoryBytes : null,
                    Source: InventorySource.DXGI));
            }

            // WMI 里有而 DXGI 没报到的适配器（如某些虚拟显示输出）也保留，但 VRAM 不取
            // AdapterRAM（>4GB 截断不可信）。
            for (var i = 0; i < videoControllers.Count; i++)
            {
                if (usedControllerIndexes.Contains(i))
                {
                    continue;
                }

                var controller = videoControllers[i];
                var name = controller.String("Name");
                if (name is null)
                {
                    continue;
                }

                results.Add(new GpuInventoryInfo(
                    Name: name,
                    Vendor: controller.String("AdapterCompatibility"),
                    VendorId: null,
                    DeviceId: null,
                    PnpDeviceId: controller.String("PNPDeviceID"),
                    DriverVersion: controller.String("DriverVersion"),
                    DriverDateUtc: ParseDriverDate(controller.String("DriverDate")),
                    DedicatedVideoMemoryBytes: null,
                    SharedSystemMemoryBytes: null,
                    Source: InventorySource.Wmi));
            }

            return results;
        }

        internal static int FindController(
            IReadOnlyList<IInventoryRow> videoControllers, string description, HashSet<int> used)
        {
            for (var i = 0; i < videoControllers.Count; i++)
            {
                if (used.Contains(i))
                {
                    continue;
                }

                var name = videoControllers[i].String("Name");
                if (name is not null &&
                    (name.Contains(description, StringComparison.OrdinalIgnoreCase)
                        || description.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Win32_VideoController.DriverDate 是 DMTF 日期字符串。</summary>
        internal static DateTimeOffset? ParseDriverDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
            {
                return null;
            }

            if (DateTimeOffset.TryParseExact(
                    value[..8],
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            return null;
        }

        internal static string? VendorIdToName(ushort vendorId) => vendorId switch
        {
            0x10DE => "NVIDIA",
            0x8086 => "Intel",
            0x1002 or 0x1022 => "AMD",
            _ => null,
        };
    }
}
