using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Diagnostics;

/// <summary>
/// Tiny append-only startup breadcrumb. It intentionally records lifecycle
/// stages only; no prompts, user logs, credentials, or hardware identifiers are
/// ever written here. A logging failure is always non-fatal.
/// </summary>
public static class StartupBreadcrumbLogger
{
    private static readonly object WriteGate = new();
    private static readonly HashSet<string> WrittenOnce = new(StringComparer.Ordinal);

    public static void WriteOnce(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return;
        }

        lock (WriteGate)
        {
            if (!WrittenOnce.Add(stage.Trim()))
            {
                return;
            }
        }

        Write(stage);
    }

    public static void Write(string stage, string? logsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return;
        }

        try
        {
            logsDirectory ??= ApplicationDataPaths.Default.LogsDirectory;
            Directory.CreateDirectory(logsDirectory);
            var path = Path.Combine(
                logsDirectory,
                $"startup-{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var version = ApplicationVersionInfo.Current;
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:O} | {Environment.ProcessId} | {version} | {stage.Trim()}{Environment.NewLine}");

            lock (WriteGate)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 256,
                    options: FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception exception)
        {
            // Breadcrumbs are diagnostics only and must never become a startup
            // dependency. Keep the failure out of the user-facing path.
            Trace.WriteLine($"StartupBreadcrumbLogger write failed: {exception.Message}");
        }
    }
}
