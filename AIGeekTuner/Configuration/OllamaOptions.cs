namespace AIGeekTuner.Configuration
{
    public sealed class OllamaOptions
    {
        public const string DefaultBaseUrl = "http://localhost:11434";

        public const string DefaultModelName = "qwen3:8b";

        public const int DefaultTimeoutSeconds = 300;

        public const bool DefaultUseJsonFormat = true;

        public string BaseUrl { get; init; } = DefaultBaseUrl;

        public string ModelName { get; init; } = DefaultModelName;

        public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

        /// <summary>
        /// 请求 /api/chat 时附带 Ollama 官方 JSON mode（顶层 "format":"json"），
        /// 由模型服务侧约束输出为合法 JSON；极旧版本 Ollama 不支持时可置为 false。
        /// </summary>
        public bool UseJsonFormat { get; init; } = DefaultUseJsonFormat;
    }
}
