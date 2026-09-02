using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate D/K：CPU / 主板 / BIOS / OS 的确定性映射。
    /// 输入是 Win32_Processor / Win32_BaseBoard / Win32_BIOS /
    /// Win32_OperatingSystem / Win32_CacheMemory 的 IInventoryRow。
    /// 占位符统一走 HardwarePlaceholderFilter（经 InventoryRowReader.String 已过滤）。
    /// </summary>
    public static class SystemInventoryMappers
    {
        public static CpuInventoryInfo? MapCpu(
            IReadOnlyList<IInventoryRow> processors,
            IReadOnlyList<IInventoryRow> cacheMemory)
        {
            if (processors.Count == 0)
            {
                return null;
            }

            string? name = null;
            string? manufacturer = null;
            uint? physicalCores = null;
            uint? logicalCores = null;
            uint? maxClock = null;
            uint? baseClock = null;
            bool? virtualization = null;

            foreach (var row in processors)
            {
                name ??= row.String("Name");
                manufacturer ??= row.String("Manufacturer");

                var cores = row.UInt32("NumberOfCores");
                if (cores.HasValue)
                {
                    physicalCores = (physicalCores ?? 0) + cores.Value;
                }

                var logical = row.UInt32("NumberOfLogicalProcessors");
                if (logical.HasValue)
                {
                    logicalCores = (logicalCores ?? 0) + logical.Value;
                }

                var clock = row.UInt32("MaxClockSpeed");
                if (clock.HasValue && (!maxClock.HasValue || clock.Value > maxClock.Value))
                {
                    maxClock = clock.Value;
                }

                baseClock ??= row.UInt32("CurrentClockSpeed");
                virtualization ??= row.Boolean("VirtualizationFirmwareEnabled");
            }

            var architecture = MapArchitecture(processors[0].UInt32("Architecture"));
            var (l2, l3) = MapCacheSizes(cacheMemory);

            return new CpuInventoryInfo(
                Name: name,
                Manufacturer: manufacturer,
                Architecture: architecture,
                PhysicalCores: physicalCores,
                LogicalCores: logicalCores,
                MaxClockSpeedMHz: maxClock,
                BaseClockSpeedMHz: baseClock,
                L2CacheSizeKB: l2,
                L3CacheSizeKB: l3,
                VirtualizationFirmwareEnabled: virtualization,
                Source: InventorySource.Wmi);
        }

        /// <summary>Win32_CacheMemory.Level：3 = L2，4 = L3（SMBIOS 编码）；取同级别最大容量。</summary>
        internal static (uint? L2KB, uint? L3KB) MapCacheSizes(IReadOnlyList<IInventoryRow> cacheMemory)
        {
            uint? l2 = null;
            uint? l3 = null;
            foreach (var row in cacheMemory)
            {
                var level = row.UInt32("Level");
                var size = row.UInt32("InstalledSize");
                if (!size.HasValue || size.Value == 0)
                {
                    continue;
                }

                if (level == 3 && (!l2.HasValue || size.Value > l2.Value))
                {
                    l2 = size.Value;
                }
                else if (level == 4 && (!l3.HasValue || size.Value > l3.Value))
                {
                    l3 = size.Value;
                }
            }

            return (l2, l3);
        }

        internal static string? MapArchitecture(uint? architecture) => architecture switch
        {
            0 => "x86",
            5 => "arm",
            6 => "arm64",
            9 => "x64",
            _ => null, // 不确定就不显示，不填 Unknown。
        };

        public static MotherboardInventoryInfo? MapMotherboard(IReadOnlyList<IInventoryRow> baseBoards)
        {
            foreach (var row in baseBoards)
            {
                var manufacturer = row.String("Manufacturer");
                var product = row.String("Product");
                if (manufacturer is null && product is null)
                {
                    continue;
                }

                return new MotherboardInventoryInfo(
                    Manufacturer: manufacturer,
                    Product: product,
                    Version: row.String("Version"),
                    SerialNumber: HardwarePlaceholderFilter.SanitizeSerialNumber(row.String("SerialNumber")),
                    Source: InventorySource.Wmi);
            }

            return null;
        }

        public static BiosInventoryInfo? MapBios(IReadOnlyList<IInventoryRow> bios)
        {
            foreach (var row in bios)
            {
                var manufacturer = row.String("Manufacturer");
                var version = row.String("SMBIOSBIOSVersion");
                if (manufacturer is null && version is null)
                {
                    continue;
                }

                var major = row.UInt32("SMBIOSMajorVersion");
                var minor = row.UInt32("SMBIOSMinorVersion");
                var smbiosVersion = major.HasValue && minor.HasValue
                    ? $"{major.Value}.{minor.Value}"
                    : null;

                return new BiosInventoryInfo(
                    Manufacturer: manufacturer,
                    SmbiosBiosVersion: version,
                    ReleaseDateUtc: row.DateTimeUtc("ReleaseDate"),
                    SmbiosVersion: smbiosVersion,
                    Source: InventorySource.Wmi);
            }

            return null;
        }

        public static OsInventoryInfo? MapOs(IReadOnlyList<IInventoryRow> operatingSystems, string? computerName)
        {
            foreach (var row in operatingSystems)
            {
                var name = row.String("Caption");
                if (name is null && computerName is null)
                {
                    continue;
                }

                return new OsInventoryInfo(
                    Name: name,
                    Version: row.String("Version"),
                    Architecture: row.String("OSArchitecture"),
                    ComputerName: computerName, // 机器名非个人信息（Gate K），不取用户名。
                    LastBootUtc: row.DateTimeUtc("LastBootUpTime"),
                    Source: InventorySource.Wmi);
            }

            return null;
        }
    }
}
