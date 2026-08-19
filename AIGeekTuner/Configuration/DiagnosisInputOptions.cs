namespace AIGeekTuner.Configuration
{
    public sealed class DiagnosisInputOptions
    {
        public int MaxFaultLogCharacters { get; init; } = 12_000;

        public string OutputLanguage { get; init; } = "简体中文";
    }
}
