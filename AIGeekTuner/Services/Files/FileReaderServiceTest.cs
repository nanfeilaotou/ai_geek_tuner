using System.IO;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Files
{
    public static class FileReaderServiceTest
    {
        public static async Task<FaultLog> RunAsync(
            IFileReaderService fileReaderService,
            string path,
            string expectedContent,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(fileReaderService);
            ArgumentNullException.ThrowIfNull(output);

            var faultLog = await fileReaderService.ReadAsync(path, cancellationToken);
            if (!string.Equals(faultLog.Content, expectedContent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("读取到的日志内容与预期不一致。");
            }

            await output.WriteLineAsync($"File: {faultLog.FileName}");
            await output.WriteLineAsync($"Size: {faultLog.FileSizeBytes} bytes");
            await output.WriteLineAsync($"Encoding: {faultLog.EncodingName}");
            await output.WriteLineAsync($"Source: {faultLog.SourceType}");

            return faultLog;
        }
    }
}
