using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Hardware
{
    public sealed class WmiHardwareDetectionService : IHardwareDetectionService
    {
        private const string WmiScope = @"root\CIMV2";

        public async Task<HardwareInfo> DetectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cpuTask = QuerySafelyAsync(
                QueryCpu,
                CpuSnapshot.Unknown,
                cancellationToken);
            var gpuTask = QuerySafelyAsync(
                QueryGpuNames,
                (IReadOnlyList<string>)[HardwareInfo.UnknownValue],
                cancellationToken);
            var memoryTask = QuerySafelyAsync(
                QueryMemory,
                MemorySnapshot.Unknown,
                cancellationToken);
            var motherboardTask = QuerySafelyAsync(
                QueryMotherboard,
                MotherboardSnapshot.Unknown,
                cancellationToken);
            var operatingSystemTask = QuerySafelyAsync(
                QueryOperatingSystem,
                OperatingSystemSnapshot.Unknown,
                cancellationToken);
            var storageTask = QuerySafelyAsync(
                QueryStorageDevices,
                (IReadOnlyList<StorageDeviceInfo>)Array.Empty<StorageDeviceInfo>(),
                cancellationToken);

            await Task.WhenAll(
                cpuTask,
                gpuTask,
                memoryTask,
                motherboardTask,
                operatingSystemTask,
                storageTask);

            var cpu = await cpuTask;
            var memory = await memoryTask;
            var motherboard = await motherboardTask;
            var operatingSystem = await operatingSystemTask;

            return new HardwareInfo
            {
                CpuName = cpu.Name,
                CpuPhysicalCoreCount = cpu.PhysicalCoreCount,
                CpuLogicalProcessorCount = cpu.LogicalProcessorCount,
                CpuMaxClockSpeedMHz = cpu.MaxClockSpeedMHz,
                GpuNames = await gpuTask,
                TotalMemoryBytes = memory.TotalBytes,
                MemoryManufacturers = memory.Manufacturers,
                MemorySpeedsMHz = memory.SpeedsMHz,
                MemoryModuleCount = memory.ModuleCount,
                MemoryType = HardwareInfo.UnknownValue,
                MotherboardManufacturer = motherboard.Manufacturer,
                MotherboardProduct = motherboard.Product,
                StorageDevices = await storageTask,
                OperatingSystemName = operatingSystem.Name,
                OperatingSystemVersion = operatingSystem.Version,
                OperatingSystemArchitecture = operatingSystem.Architecture,
                DetectedAt = DateTimeOffset.Now
            };
        }

        private static CpuSnapshot QueryCpu()
        {
            using var searcher = CreateSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            using var results = searcher.Get();

            string? name = null;
            ulong physicalCoreCount = 0;
            ulong logicalProcessorCount = 0;
            uint? maxClockSpeedMHz = null;
            var hasPhysicalCoreCount = false;
            var hasLogicalProcessorCount = false;

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    name ??= ReadString(item, "Name");

                    var physicalCores = ReadUInt32(item, "NumberOfCores");
                    if (physicalCores.HasValue)
                    {
                        physicalCoreCount += physicalCores.Value;
                        hasPhysicalCoreCount = true;
                    }

                    var logicalProcessors = ReadUInt32(
                        item,
                        "NumberOfLogicalProcessors");
                    if (logicalProcessors.HasValue)
                    {
                        logicalProcessorCount += logicalProcessors.Value;
                        hasLogicalProcessorCount = true;
                    }

                    var clockSpeed = ReadUInt32(item, "MaxClockSpeed");
                    if (clockSpeed.HasValue &&
                        (!maxClockSpeedMHz.HasValue ||
                         clockSpeed.Value > maxClockSpeedMHz.Value))
                    {
                        maxClockSpeedMHz = clockSpeed;
                    }
                }
            }

            return new CpuSnapshot(
                name ?? HardwareInfo.UnknownValue,
                ToUInt32OrNull(physicalCoreCount, hasPhysicalCoreCount),
                ToUInt32OrNull(logicalProcessorCount, hasLogicalProcessorCount),
                maxClockSpeedMHz);
        }

        private static IReadOnlyList<string> QueryGpuNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var searcher = CreateSearcher("SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var name = ReadString(item, "Name");
                    if (name is not null)
                    {
                        names.Add(name);
                    }
                }
            }

            return names.Count == 0
                ? [HardwareInfo.UnknownValue]
                : names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static MemorySnapshot QueryMemory()
        {
            ulong totalBytes = 0;
            var hasCapacity = false;
            var manufacturers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var speeds = new HashSet<uint>();
            var moduleCount = 0;

            using var searcher = CreateSearcher(
                "SELECT Capacity, Manufacturer, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    moduleCount++;
                    var capacity = ReadUInt64(item, "Capacity");
                    if (capacity.HasValue)
                    {
                        totalBytes = checked(totalBytes + capacity.Value);
                        hasCapacity = true;
                    }

                    var manufacturer = ReadString(item, "Manufacturer");
                    if (manufacturer is not null)
                    {
                        manufacturers.Add(manufacturer);
                    }

                    var speed = ReadUInt32(item, "ConfiguredClockSpeed")
                        ?? ReadUInt32(item, "Speed");
                    if (speed is > 0)
                    {
                        speeds.Add(speed.Value);
                    }
                }
            }

            return new MemorySnapshot(
                hasCapacity ? totalBytes : null,
                manufacturers.Count == 0
                    ? [HardwareInfo.UnknownValue]
                    : manufacturers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                speeds.Order().ToArray(),
                moduleCount > 0 ? moduleCount : null);
        }

        private static IReadOnlyList<StorageDeviceInfo> QueryStorageDevices()
        {
            var devices = new List<StorageDeviceInfo>();
            using var searcher = CreateSearcher(
                "SELECT Model, Size, MediaType FROM Win32_DiskDrive");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    devices.Add(new StorageDeviceInfo
                    {
                        Model = ReadString(item, "Model")
                            ?? HardwareInfo.UnknownValue,
                        CapacityBytes = ReadUInt64(item, "Size"),
                        MediaType = NormalizeStorageMediaType(
                            ReadString(item, "MediaType"))
                    });
                }
            }

            return devices
                .OrderBy(device => device.Model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static MotherboardSnapshot QueryMotherboard()
        {
            using var searcher = CreateSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    return new MotherboardSnapshot(
                        ReadString(item, "Manufacturer") ?? HardwareInfo.UnknownValue,
                        ReadString(item, "Product") ?? HardwareInfo.UnknownValue);
                }
            }

            return MotherboardSnapshot.Unknown;
        }

        private static OperatingSystemSnapshot QueryOperatingSystem()
        {
            using var searcher = CreateSearcher(
                "SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    return new OperatingSystemSnapshot(
                        ReadString(item, "Caption") ?? HardwareInfo.UnknownValue,
                        ReadString(item, "Version") ?? HardwareInfo.UnknownValue,
                        ReadString(item, "OSArchitecture") ?? HardwareInfo.UnknownValue);
                }
            }

            return OperatingSystemSnapshot.Unknown;
        }

        private static ManagementObjectSearcher CreateSearcher(string query) =>
            new(WmiScope, query);

        private static string? ReadString(
            ManagementBaseObject item,
            string propertyName)
        {
            var value = item[propertyName];
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static ulong? ReadUInt64(
            ManagementBaseObject item,
            string propertyName)
        {
            var value = item[propertyName];
            if (value is null)
            {
                return null;
            }

            return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }

        private static uint? ReadUInt32(
            ManagementBaseObject item,
            string propertyName)
        {
            var value = item[propertyName];
            if (value is null)
            {
                return null;
            }

            return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }

        private static uint? ToUInt32OrNull(ulong value, bool hasValue) =>
            hasValue && value <= uint.MaxValue ? (uint)value : null;

        private static string NormalizeStorageMediaType(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return HardwareInfo.UnknownValue;
            }

            if (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains(
                    "Solid State",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "SSD";
            }

            if (string.Equals(
                    mediaType.Trim(),
                    "HDD",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "HDD";
            }

            // Win32_DiskDrive frequently reports "Fixed hard disk media" for
            // both SSDs and HDDs, so that generic value must not be classified.
            return HardwareInfo.UnknownValue;
        }

        private static async Task<T> QuerySafelyAsync<T>(
            Func<T> query,
            T fallback,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = query();
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedWmiException(exception))
            {
                return fallback;
            }
        }

        private static bool IsExpectedWmiException(Exception exception) =>
            exception is ManagementException
            or COMException
            or UnauthorizedAccessException
            or InvalidOperationException
            or FormatException
            or OverflowException;

        private sealed record CpuSnapshot(
            string Name,
            uint? PhysicalCoreCount,
            uint? LogicalProcessorCount,
            uint? MaxClockSpeedMHz)
        {
            public static CpuSnapshot Unknown { get; } = new(
                HardwareInfo.UnknownValue,
                null,
                null,
                null);
        }

        private sealed record MemorySnapshot(
            ulong? TotalBytes,
            IReadOnlyList<string> Manufacturers,
            IReadOnlyList<uint> SpeedsMHz,
            int? ModuleCount)
        {
            public static MemorySnapshot Unknown { get; } = new(
                null,
                [HardwareInfo.UnknownValue],
                Array.Empty<uint>(),
                null);
        }

        private sealed record MotherboardSnapshot(string Manufacturer, string Product)
        {
            public static MotherboardSnapshot Unknown { get; } = new(
                HardwareInfo.UnknownValue,
                HardwareInfo.UnknownValue);
        }

        private sealed record OperatingSystemSnapshot(
            string Name,
            string Version,
            string Architecture)
        {
            public static OperatingSystemSnapshot Unknown { get; } = new(
                HardwareInfo.UnknownValue,
                HardwareInfo.UnknownValue,
                HardwareInfo.UnknownValue);
        }
    }
}
