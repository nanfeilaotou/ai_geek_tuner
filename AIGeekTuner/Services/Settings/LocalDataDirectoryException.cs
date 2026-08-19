namespace AIGeekTuner.Services.Settings
{
    public sealed class LocalDataDirectoryException : Exception
    {
        public LocalDataDirectoryException(
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
