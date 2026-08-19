namespace AIGeekTuner.Models
{
    public sealed class HardwareSensorSnapshot
    {
        public double? CpuTemperatureCelsius { get; init; }

        public double? CpuLoadPercent { get; init; }

        public double? CpuClockMHz { get; init; }

        public double? CpuPackagePowerWatts { get; init; }

        public double? CpuVoltageVolts { get; init; }

        public string? GpuName { get; init; }

        public double? GpuTemperatureCelsius { get; init; }

        public double? GpuLoadPercent { get; init; }

        public double? GpuCoreClockMHz { get; init; }

        public double? GpuMemoryClockMHz { get; init; }

        public double? GpuPowerWatts { get; init; }

        public double? MemoryLoadPercent { get; init; }

        public IReadOnlyList<StorageTemperatureReading> StorageTemperatures { get; init; } =
            Array.Empty<StorageTemperatureReading>();

        public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

        public bool HasAnyValue =>
            CpuTemperatureCelsius.HasValue
            || CpuLoadPercent.HasValue
            || CpuClockMHz.HasValue
            || CpuPackagePowerWatts.HasValue
            || CpuVoltageVolts.HasValue
            || GpuTemperatureCelsius.HasValue
            || GpuLoadPercent.HasValue
            || GpuCoreClockMHz.HasValue
            || GpuMemoryClockMHz.HasValue
            || GpuPowerWatts.HasValue
            || MemoryLoadPercent.HasValue
            || StorageTemperatures.Count > 0;
    }
}
