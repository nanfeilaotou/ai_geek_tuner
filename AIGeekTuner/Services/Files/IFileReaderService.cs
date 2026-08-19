using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Files
{
    public interface IFileReaderService
    {
        Task<FaultLog> ReadAsync(
            string path,
            CancellationToken cancellationToken = default);
    }
}
