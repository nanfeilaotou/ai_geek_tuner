namespace AIGeekTuner.Configuration
{
    public sealed class DiagnosisInputOptions
    {
        public const int DefaultMaxFaultLogCharacters = 12_000;

        public int MaxFaultLogCharacters { get; init; } = DefaultMaxFaultLogCharacters;
    }
}
