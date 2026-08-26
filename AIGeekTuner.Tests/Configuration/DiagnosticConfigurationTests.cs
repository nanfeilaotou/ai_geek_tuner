using AIGeekTuner.Configuration;

namespace AIGeekTuner.Tests.Configuration;

public class ApplicationSettingsValidatorTests
{
    [Fact]
    public void Validate_ValidSettings_ReturnsNoErrors()
    {
        Assert.Empty(ApplicationSettingsValidator.Validate(new ApplicationSettings()));
    }

    [Theory]
    [InlineData("ftp://x")]
    [InlineData("localhost:11434")]
    [InlineData("")]
    public void Validate_InvalidBaseUrl_ReportsError(string baseUrl)
    {
        var errors = ApplicationSettingsValidator.Validate(
            new ApplicationSettings { OllamaBaseUrl = baseUrl });

        Assert.Contains(errors, error => error.Contains("服务地址"));
    }

    [Fact]
    public void Validate_BlankModelName_ReportsError()
    {
        var errors = ApplicationSettingsValidator.Validate(
            new ApplicationSettings { OllamaModelName = "   " });

        Assert.Contains(errors, error => error.Contains("模型名称"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3601)]
    public void Validate_TimeoutOutsideRange_ReportsError(int timeoutSeconds)
    {
        var errors = ApplicationSettingsValidator.Validate(
            new ApplicationSettings { OllamaTimeoutSeconds = timeoutSeconds });

        Assert.Contains(errors, error => error.Contains("请求超时"));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(200_001)]
    public void Validate_MaxLogLengthOutsideRange_ReportsError(int maxLogLength)
    {
        var errors = ApplicationSettingsValidator.Validate(
            new ApplicationSettings { MaxFaultLogCharacters = maxLogLength });

        Assert.Contains(errors, error => error.Contains("诊断输入长度"));
    }
}

public class DiagnosticConfigurationStoreTests
{
    [Fact]
    public void Replace_NextSnapshotReturnsNewConfigurationImmediately()
    {
        var store = CreateStore(baseUrl: "http://a:11434");
        var updated = SnapshotFor(baseUrl: "http://b:11434");

        store.Replace(updated);

        Assert.Same(updated, store.Snapshot());
        Assert.Equal("http://b:11434", store.Snapshot().Ollama.BaseUrl);
    }

    [Fact]
    public void Snapshot_TakenBeforeReplace_StaysUnchangedAfterReplace()
    {
        var store = CreateStore(baseUrl: "http://a:11434");
        var runningSnapshot = store.Snapshot();

        store.Replace(SnapshotFor(baseUrl: "http://b:11434"));

        // 正在运行的诊断持有的旧快照必须完全不受新配置影响。
        Assert.Equal("http://a:11434", runningSnapshot.Ollama.BaseUrl);
        Assert.Equal(OllamaOptions.DefaultModelName, runningSnapshot.Ollama.ModelName);
    }

    [Fact]
    public async Task ConcurrentReplaceAndRead_NeverThrowsAndAlwaysYieldsCompleteSnapshot()
    {
        var store = CreateStore(baseUrl: "http://a:11434");
        var replacements = Enumerable.Range(0, 200)
            .Select(index => SnapshotFor($"http://node{index}:11434"))
            .ToArray();

        await Task.WhenAll(
            Task.Run(() =>
            {
                foreach (var configuration in replacements)
                {
                    store.Replace(configuration);
                }
            }),
            Task.Run(() =>
            {
                for (var index = 0; index < replacements.Length; index++)
                {
                    // 每次读取都必须是某个完整版本，而不是半更新的对象。
                    var snapshot = store.Snapshot();
                    Assert.NotNull(snapshot.Ollama);
                    Assert.NotNull(snapshot.Input);
                }
            }));
    }

    private static DiagnosticConfigurationStore CreateStore(string baseUrl) =>
        new(SnapshotFor(baseUrl));

    private static DiagnosticConfiguration SnapshotFor(string baseUrl) =>
        new(
            new OllamaOptions { BaseUrl = baseUrl },
            new DiagnosisInputOptions());
}
