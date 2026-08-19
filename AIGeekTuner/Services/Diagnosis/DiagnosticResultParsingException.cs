namespace AIGeekTuner.Services.Diagnosis
{
    public sealed class DiagnosticResultParsingException : Exception
    {
        public DiagnosticResultParsingException(string message)
            : base(message)
        {
        }

        public DiagnosticResultParsingException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
