using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Diagnosis
{
    public interface IDiagnosisService
    {
        Task<DiagnosisOutcome> DiagnoseAsync(
            DiagnosticRequest request,
            CancellationToken cancellationToken = default);
    }
}
