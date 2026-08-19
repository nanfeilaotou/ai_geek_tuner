namespace AIGeekTuner.Configuration
{
    public sealed class OllamaOptions
    {
        public string BaseUrl { get; init; } = "http://localhost:11434";

        public string ModelName { get; init; } = "qwen3:8b";

        public int TimeoutSeconds { get; init; } = 300;
    }
}
