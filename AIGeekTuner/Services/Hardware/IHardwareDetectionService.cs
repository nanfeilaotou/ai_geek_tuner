using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Hardware
{
    public interface IHardwareDetectionService
    {
        Task<HardwareInfo> DetectAsync(CancellationToken cancellationToken = default);
    }
}
