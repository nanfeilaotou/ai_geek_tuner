using AIGeekTuner.Models;

namespace AIGeekTuner.Services.History
{
    public interface IDiagnosisHistoryService
    {
        Task<DiagnosisRecord> SaveAsync(
            DiagnosisOutcome outcome,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<DiagnosisRecord>> GetRecordsAsync(
            CancellationToken cancellationToken);

        Task<DiagnosisOutcome> LoadOutcomeAsync(
            DiagnosisRecord record,
            CancellationToken cancellationToken);

        Task DeleteAsync(
            Guid diagnosisId,
            CancellationToken cancellationToken = default);
    }
}
