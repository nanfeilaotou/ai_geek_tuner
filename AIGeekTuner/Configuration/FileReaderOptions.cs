namespace AIGeekTuner.Configuration
{
    public sealed class FileReaderOptions
    {
        public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    }
}
