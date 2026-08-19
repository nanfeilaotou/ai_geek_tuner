namespace AIGeekTuner.Services.History
{
    public sealed class DiagnosisHistoryException : Exception
    {
        public DiagnosisHistoryException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
