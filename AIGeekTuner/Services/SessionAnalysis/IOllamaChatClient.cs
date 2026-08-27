using AIGeekTuner.Services.Telemetry.Recording;
namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 极小 Ollama /api/chat 客户端 seam（§5）：只负责 messages+model/format/options →
    /// text response，以及超时与取消。V1 OllamaService 保持不动（§7）。
    /// </summary>
    public interface IOllamaChatClient
    {
        Task<string> ChatAsync(
            string systemPrompt,
            string userPrompt,
            string? formatJsonSchema,
            bool? think,
            CancellationToken cancellationToken);
    }
}


