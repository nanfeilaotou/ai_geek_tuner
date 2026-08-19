namespace AIGeekTuner.Services.Reports
{
    public sealed class ReportExportException : Exception
    {
        public ReportExportException(
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
