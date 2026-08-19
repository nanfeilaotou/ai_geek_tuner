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
    }
}
