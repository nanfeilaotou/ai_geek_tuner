namespace AIGeekTuner.Services.Settings
{
    public sealed class ApplicationSettingsException : Exception
    {
        public ApplicationSettingsException(
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
