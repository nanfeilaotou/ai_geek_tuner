namespace AIGeekTuner.Models
{
    public sealed class StorageTemperatureReading
    {
        public required string StorageName { get; init; }

        public required double TemperatureCelsius { get; init; }
    }
}
