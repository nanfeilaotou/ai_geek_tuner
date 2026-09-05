using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Telemetry.Recording;
namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 极小 chat 客户端 seam（§5）：只负责 messages+model/format/options →
    /// text response，以及超时与取消。
    /// V2-M5.1B：传入 <paramref name="runtime"/>（一次分析的运行时快照，Gate D）时
    /// 走 provider-aware transport；null 时保持 legacy Ollama 行为（兼容旧调用/测试）。
    /// </summary>
    public interface IOllamaChatClient
    {
        Task<string> ChatAsync(
            AiRuntimeSnapshot? runtime,
            string systemPrompt,
            string userPrompt,
            string? formatJsonSchema,
            bool? think,
            CancellationToken cancellationToken);
    }
}


