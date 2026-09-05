namespace AIGeekTuner.Services.Diagnosis
{
    public sealed class DiagnosisException : Exception
    {
        public DiagnosisException(DiagnosisError error, string message)
            : base(message)
        {
            Error = error;
        }

        public DiagnosisException(
            DiagnosisError error,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            Error = error;
        }

        public DiagnosisError Error { get; }

        // ---- V2-M5.1B（Gate L）：失败发生时已知的运行时元数据；快照尚未建立时为 null。 ----

        public string? ModelName { get; init; }

        public string? ProviderName { get; init; }
    }
}
