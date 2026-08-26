using System.IO;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.Settings;

/// <summary>
/// settings.json 持久化契约：默认值、旧版本兼容、损坏自愈、
/// 校验拦截与“磁盘成功才切换内存快照”的顺序保证。
/// </summary>
public class JsonApplicationSettingsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsFromSingleSource()
    {
        var service = CreateService();

        Assert.True(service.Current.AutoSaveDiagnosisHistory);
        Assert.Equal(OllamaOptions.DefaultBaseUrl, service.Current.OllamaBaseUrl);
        Assert.Equal(OllamaOptions.DefaultModelName, service.Current.OllamaModelName);
        Assert.Equal(OllamaOptions.DefaultTimeoutSeconds, service.Current.OllamaTimeoutSeconds);
        Assert.Equal(DiagnosisInputOptions.DefaultMaxFaultLogCharacters,
            service.Current.MaxFaultLogCharacters);
        Assert.Equal(OllamaOptions.DefaultUseJsonFormat, service.Current.UseJsonFormat);
    }

    [Fact]
    public async Task Load_LegacyFileWithAutoSaveOnly_FillsNewFieldsWithDefaults()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        Directory.CreateDirectory(paths.SettingsDirectory);
        await File.WriteAllTextAsync(
            paths.SettingsFilePath,
            """{ "autoSaveDiagnosisHistory": false }""");

        var service = new JsonApplicationSettingsService(paths);

        Assert.False(service.Current.AutoSaveDiagnosisHistory);
        Assert.Equal(OllamaOptions.DefaultModelName, service.Current.OllamaModelName);
        Assert.Equal(OllamaOptions.DefaultBaseUrl, service.Current.OllamaBaseUrl);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var service = CreateService();
        var original = ValidSettings();

        await service.SaveAsync(original, CancellationToken.None);

        var reloaded = CreateService();
        Assert.Equal(original.AutoSaveDiagnosisHistory, reloaded.Current.AutoSaveDiagnosisHistory);
        Assert.Equal(original.OllamaBaseUrl, reloaded.Current.OllamaBaseUrl);
        Assert.Equal(original.OllamaModelName, reloaded.Current.OllamaModelName);
        Assert.Equal(original.OllamaTimeoutSeconds, reloaded.Current.OllamaTimeoutSeconds);
        Assert.Equal(original.MaxFaultLogCharacters, reloaded.Current.MaxFaultLogCharacters);
        Assert.Equal(original.UseJsonFormat, reloaded.Current.UseJsonFormat);
    }

    [Fact]
    public async Task Save_OverwritesPreviousConfiguration()
    {
        var service = CreateService();
        await service.SaveAsync(ValidSettings(), CancellationToken.None);

        await service.SaveAsync(
            ValidSettings(modelName: "llama3.1:8b"), CancellationToken.None);

        Assert.Equal("llama3.1:8b", service.Current.OllamaModelName);
        Assert.Equal("llama3.1:8b", CreateService().Current.OllamaModelName);
    }

    [Fact]
    public void Load_MalformedJson_FallsBackToDefaults()
    {
        WriteRawSettings("{ broken json at all");

        var service = CreateService();

        Assert.Equal(OllamaOptions.DefaultModelName, service.Current.OllamaModelName);
        Assert.True(service.Current.AutoSaveDiagnosisHistory);
    }

    [Fact]
    public void Load_MalformedJson_CreatesCorruptBackup()
    {
        WriteRawSettings("{ definitely broken");

        CreateService();

        var directory = Path.GetDirectoryName(ExpectedSettingsPath())!;
        var backups = Directory.GetFiles(directory, "settings.corrupt-*.json");
        Assert.Single(backups);
        Assert.Contains("definitely broken", File.ReadAllText(backups[0]));
    }

    [Fact]
    public async Task Save_InvalidBaseUrl_IsRejectedAndNothingChanges()
    {
        var service = CreateService();
        await service.SaveAsync(ValidSettings(), CancellationToken.None);
        var invalid = ValidSettings(baseUrl: "ftp://not-http");

        var exception = await Assert.ThrowsAsync<ApplicationSettingsException>(
            () => service.SaveAsync(invalid, CancellationToken.None));

        Assert.Contains("服务地址", exception.Message);
        Assert.NotEqual("ftp://not-http", service.Current.OllamaBaseUrl);
        Assert.False(CreateService().Current.OllamaBaseUrl.StartsWith("ftp"));
    }

    [Fact]
    public async Task Save_TimeOutOfRange_IsRejected()
    {
        var service = CreateService();
        var invalid = ValidSettings(timeoutSeconds: 2);

        var exception = await Assert.ThrowsAsync<ApplicationSettingsException>(
            () => service.SaveAsync(invalid, CancellationToken.None));

        Assert.Contains("请求超时", exception.Message);
    }

    [Fact]
    public async Task Save_MaxLogLengthOutOfRange_IsRejected()
    {
        var service = CreateService();
        var invalid = ValidSettings(maxLogLength: int.MaxValue);

        var exception = await Assert.ThrowsAsync<ApplicationSettingsException>(
            () => service.SaveAsync(invalid, CancellationToken.None));

        Assert.Contains("诊断输入长度", exception.Message);
    }

    [Fact]
    public async Task Save_AcceptsLanOllamaAddress()
    {
        var service = CreateService();

        // 局域网 Ollama 是合理场景，不得强制 localhost。
        await service.SaveAsync(
            ValidSettings(baseUrl: "http://192.168.1.50:11434"), CancellationToken.None);

        Assert.Equal("http://192.168.1.50:11434", service.Current.OllamaBaseUrl);
    }

    private static ApplicationSettings ValidSettings(
        string baseUrl = OllamaOptions.DefaultBaseUrl,
        string modelName = OllamaOptions.DefaultModelName,
        int timeoutSeconds = OllamaOptions.DefaultTimeoutSeconds,
        int maxLogLength = DiagnosisInputOptions.DefaultMaxFaultLogCharacters)
    {
        return new ApplicationSettings
        {
            AutoSaveDiagnosisHistory = true,
            OllamaBaseUrl = baseUrl,
            OllamaModelName = modelName,
            OllamaTimeoutSeconds = timeoutSeconds,
            MaxFaultLogCharacters = maxLogLength,
            UseJsonFormat = true
        };
    }

    private JsonApplicationSettingsService CreateService()
    {
        return new JsonApplicationSettingsService(HistoryTestFactory.CreatePaths(_temp));
    }

    private string ExpectedSettingsPath()
    {
        return HistoryTestFactory.CreatePaths(_temp).SettingsFilePath;
    }

    private void WriteRawSettings(string json)
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(paths.SettingsFilePath, json);
    }

    public void Dispose() => _temp.Dispose();
}
