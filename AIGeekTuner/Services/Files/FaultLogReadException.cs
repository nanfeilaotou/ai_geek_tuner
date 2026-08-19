namespace AIGeekTuner.Services.Files
{
    public sealed class FaultLogReadException : Exception
    {
        public FaultLogReadException(FaultLogReadError error, string message)
            : base(message)
        {
            Error = error;
        }

        public FaultLogReadException(
            FaultLogReadError error,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            Error = error;
        }

        public FaultLogReadError Error { get; }
    }
}
