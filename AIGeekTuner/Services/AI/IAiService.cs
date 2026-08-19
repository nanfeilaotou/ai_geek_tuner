using AIGeekTuner.Models;

namespace AIGeekTuner.Services.AI
{
    public interface IAiService
    {
        Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

        Task<string> SendMessageAsync(
            string message,
            CancellationToken cancellationToken = default);

        Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            CancellationToken cancellationToken = default);
    }
}
