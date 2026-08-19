using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Knowledge
{
    public interface IDiagnosticKnowledgeService
    {
        Task<IReadOnlyList<DiagnosticKnowledgeEntry>> FindMatchesAsync(
            FaultLog faultLog,
            CancellationToken cancellationToken = default);
    }
}
