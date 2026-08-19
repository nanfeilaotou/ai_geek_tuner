using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Hardware
{
    public interface IHardwareSensorService
    {
        Task<HardwareSensorSnapshot> ReadAsync(
            CancellationToken cancellationToken = default);
    }
}
