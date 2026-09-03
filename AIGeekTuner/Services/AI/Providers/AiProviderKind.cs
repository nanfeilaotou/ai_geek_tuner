namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// Provider 协议类型。第一版只有两种：
    /// Ollama 原生协议与 OpenAI 兼容协议（LM Studio 等只是预设，不是独立 Kind）。
    /// </summary>
    public enum AiProviderKind
    {
        OllamaNative,

        OpenAiCompatible
    }
}
