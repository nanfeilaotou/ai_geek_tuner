using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.AI
{
    public interface IAiService
    {
        Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

        Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 显式提供本次诊断使用的配置快照；
        /// 与 Prompt 构建所用配置来自同一次快照获取，保证一次诊断一份配置。
        /// </summary>
        Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            OllamaOptions options,
            CancellationToken cancellationToken = default);
    }
}
