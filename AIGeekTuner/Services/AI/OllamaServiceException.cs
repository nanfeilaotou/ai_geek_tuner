namespace AIGeekTuner.Services.AI
{
    public sealed class OllamaServiceException : Exception
    {
        public OllamaServiceException(string message)
            : base(message)
        {
        }

        public OllamaServiceException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
