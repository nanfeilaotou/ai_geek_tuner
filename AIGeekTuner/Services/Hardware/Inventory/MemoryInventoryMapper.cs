using System;
using System.Collections.Generic;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate E：Win32_PhysicalMemory → 每条 DIMM 独立 module。
    /// 两条模块保持独立 identity（DeviceLocator/BankLabel/PartNumber/Serial），
    /// 绝不汇总。FormFactor 只映射可靠值（8=DIMM、12=SODIMM），其余 null。
    /// 本轮无 temperature（属 M4.5B telemetry）。
    /// </summary>
    public static class MemoryInventoryMapper
    {
        public static IReadOnlyList<MemoryModuleInfo> Map(IReadOnlyList<IInventoryRow> physicalMemory)
        {
            var modules = new List<MemoryModuleInfo>(physicalMemory.Count);
            foreach (var row in physicalMemory)
            {
                var capacity = row.UInt64("Capacity");
                var manufacturer = row.String("Manufacturer");
                var locator = row.String("DeviceLocator");
                var partNumber = row.String("PartNumber");
                if (capacity is null && manufacturer is null && locator is null && partNumber is null)
                {
                    continue; // 完全无信息量的行不进入 inventory。
                }

                modules.Add(new MemoryModuleInfo(
                    DeviceLocator: locator,
                    BankLabel: row.String("BankLabel"),
                    CapacityBytes: capacity,
                    Manufacturer: manufacturer,
                    PartNumber: partNumber,
                    SerialNumber: HardwarePlaceholderFilter.SanitizeSerialNumber(row.String("SerialNumber")),
                    SpeedMHz: ZeroToNull(row.UInt32("Speed")),
                    ConfiguredClockSpeedMHz: ZeroToNull(row.UInt32("ConfiguredClockSpeed")),
                    FormFactor: MapFormFactor(row.UInt32("FormFactor")),
                    DataWidthBits: ZeroToNull(row.UInt32("DataWidth")),
                    TotalWidthBits: ZeroToNull(row.UInt32("TotalWidth")),
                    Source: InventorySource.Wmi));
            }

            return modules;
        }

        internal static string? MapFormFactor(uint? formFactor) => formFactor switch
        {
            8 => "DIMM",
            12 => "SODIMM",
            _ => null,
        };

        private static uint? ZeroToNull(uint? value) => value is > 0 ? value : null;
    }
}
