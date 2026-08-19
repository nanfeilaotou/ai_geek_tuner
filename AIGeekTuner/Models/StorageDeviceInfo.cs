namespace AIGeekTuner.Models
{
    public sealed class StorageDeviceInfo
    {
        public string Model { get; init; } = HardwareInfo.UnknownValue;

        public ulong? CapacityBytes { get; init; }

        public string MediaType { get; init; } = HardwareInfo.UnknownValue;
    }
}
