using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Reports
{
    public interface IReportExportService
    {
        Task<string> ExportAsync(
            DiagnosisOutcome outcome,
            CancellationToken cancellationToken = default);
    }
}
