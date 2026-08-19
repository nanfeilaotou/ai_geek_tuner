namespace AIGeekTuner.Models
{
    public sealed class SystemContext
    {
        public string? OperatingSystemVersion { get; init; }

        public string? CpuName { get; init; }

        public uint? CpuCoreCount { get; init; }

        public ulong? TotalMemoryBytes { get; init; }

        public DateTimeOffset? SystemBootTime { get; init; }
    }
}
