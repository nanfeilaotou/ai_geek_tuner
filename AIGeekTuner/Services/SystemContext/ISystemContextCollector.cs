using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Context
{
    public interface ISystemContextCollector
    {
        Task<SystemContext> CollectAsync(
            CancellationToken cancellationToken = default);
    }
}
