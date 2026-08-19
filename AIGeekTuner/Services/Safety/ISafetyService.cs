using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety
{
    public interface ISafetyService
    {
        Task<SafetyResult> ValidateAsync(
            DiagnosticResult result,
            CancellationToken cancellationToken = default);
    }
}
