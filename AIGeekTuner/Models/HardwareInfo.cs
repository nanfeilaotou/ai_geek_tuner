namespace AIGeekTuner.Models
{
    public sealed class HardwareInfo
    {
        public const string UnknownValue = "Unknown";

        public string CpuName { get; init; } = UnknownValue;

        public uint? CpuPhysicalCoreCount { get; init; }

        public uint? CpuLogicalProcessorCount { get; init; }

        public uint? CpuMaxClockSpeedMHz { get; init; }

        public IReadOnlyList<string> GpuNames { get; init; } = [UnknownValue];

        public ulong? TotalMemoryBytes { get; init; }

        public IReadOnlyList<string> MemoryManufacturers { get; init; } = [UnknownValue];

        public IReadOnlyList<uint> MemorySpeedsMHz { get; init; } = Array.Empty<uint>();

        public int? MemoryModuleCount { get; init; }

        public string MemoryType { get; init; } = UnknownValue;

        public string MotherboardManufacturer { get; init; } = UnknownValue;

        public string MotherboardProduct { get; init; } = UnknownValue;

        public IReadOnlyList<StorageDeviceInfo> StorageDevices { get; init; } =
            Array.Empty<StorageDeviceInfo>();

        public string OperatingSystemName { get; init; } = UnknownValue;

        public string OperatingSystemVersion { get; init; } = UnknownValue;

        public string OperatingSystemArchitecture { get; init; } = UnknownValue;

        public DateTimeOffset DetectedAt { get; init; }
    }
}
