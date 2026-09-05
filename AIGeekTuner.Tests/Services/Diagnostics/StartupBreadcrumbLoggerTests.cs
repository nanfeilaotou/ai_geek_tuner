using System.IO;
using AIGeekTuner.Services.Diagnostics;
using Xunit;

namespace AIGeekTuner.Tests.Services.Diagnostics;

public sealed class StartupBreadcrumbLoggerTests
{
    [Fact]
    public void WritesStageWithoutSensitivePayload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aigeektuner-breadcrumb-{Guid.NewGuid():N}");
        try
        {
            StartupBreadcrumbLogger.Write("TEST_READY", directory);
            var file = Directory.GetFiles(directory, "startup-*.log").Single();
            var line = File.ReadAllText(file);

            Assert.Contains("TEST_READY", line);
            Assert.Contains(Environment.ProcessId.ToString(), line);
            Assert.DoesNotContain("prompt", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api-key", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void IoFailureIsNonFatal()
    {
        var exception = Record.Exception(() =>
            StartupBreadcrumbLogger.Write("TEST_IO_FAILURE", "\0invalid-log-directory"));

        Assert.Null(exception);
    }
}
