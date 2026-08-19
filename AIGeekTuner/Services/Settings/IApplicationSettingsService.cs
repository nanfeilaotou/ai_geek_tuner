using AIGeekTuner.Configuration;

namespace AIGeekTuner.Services.Settings
{
    public interface IApplicationSettingsService
    {
        ApplicationSettings Current { get; }

        Task SetAutoSaveDiagnosisHistoryAsync(
            bool enabled,
            CancellationToken cancellationToken = default);
    }
}
