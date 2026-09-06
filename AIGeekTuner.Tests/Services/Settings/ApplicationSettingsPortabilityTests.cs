using System.IO;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.Settings;

public sealed class ApplicationSettingsPortabilityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task ExportImport_RoundTripsVersionedSettingsWithoutSecrets()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        var original = new ApplicationSettings
        {
            AutoSaveDiagnosisHistory = false,
            OllamaTimeoutSeconds = 45,
            MaxFaultLogCharacters = 20_000,
            RecordingIntervalMs = 5000,
            Voice = new VoiceSettings
            {
                Enabled = true,
                Endpoint = "http://127.0.0.1:9880",
                ReferenceAudioPath = _temp.Combine("ref.wav"),
                PromptLang = "zh",
                SpeedFactor = 1.1
            }
        };
        await service.SaveAsync(original);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var exportPath = _temp.Combine("AIGeekTuner_Settings_v1.json");

        await portability.ExportAsync(exportPath);

        var json = await File.ReadAllTextAsync(exportPath);
        Assert.Contains("schemaVersion", json);
        Assert.DoesNotContain("sk-aigeek-secret-test-123", json);

        await service.SaveAsync(new ApplicationSettings());
        var imported = await portability.ImportAsync(exportPath);
        Assert.False(imported.Settings.AutoSaveDiagnosisHistory);
        Assert.Equal(45, service.Current.OllamaTimeoutSeconds);
        Assert.Equal(5000, service.Current.RecordingIntervalMs);
        Assert.True(File.Exists(paths.SettingsFilePath + ".bak"));
    }

    [Fact]
    public async Task Import_UnknownFieldsIsForwardTolerant()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var path = _temp.Combine("future-settings.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "exportedAtUtc": "2026-09-06T00:00:00Z",
          "applicationVersion": "future",
          "settings": {
            "autoSaveDiagnosisHistory": false,
            "futurePreference": true
          }
        }
        """);

        var result = await portability.ImportAsync(path);

        Assert.False(result.Settings.AutoSaveDiagnosisHistory);
        Assert.False(service.Current.AutoSaveDiagnosisHistory);
    }

    [Fact]
    public async Task Import_UnsupportedSchemaOrInvalidSettingsLeavesCurrentUntouched()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        await service.SaveAsync(new ApplicationSettings { OllamaTimeoutSeconds = 60 });
        var before = await File.ReadAllTextAsync(paths.SettingsFilePath);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var unsupported = _temp.Combine("unsupported.json");
        await File.WriteAllTextAsync(unsupported, """
        { "schemaVersion": 99, "settings": { "ollamaTimeoutSeconds": 1 } }
        """);

        await Assert.ThrowsAsync<SettingsPortabilityException>(
            () => portability.ImportAsync(unsupported));
        Assert.Equal(60, service.Current.OllamaTimeoutSeconds);
        Assert.Equal(before, await File.ReadAllTextAsync(paths.SettingsFilePath));

        var invalid = _temp.Combine("invalid.json");
        await File.WriteAllTextAsync(invalid, """
        { "schemaVersion": 1, "settings": { "ollamaTimeoutSeconds": 1 } }
        """);
        await Assert.ThrowsAsync<SettingsPortabilityException>(
            () => portability.ImportAsync(invalid));
        Assert.Equal(60, service.Current.OllamaTimeoutSeconds);
        Assert.Equal(before, await File.ReadAllTextAsync(paths.SettingsFilePath));
    }

    [Fact]
    public async Task Import_MissingLocalVoicePathsReturnsWarningsButSucceeds()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var path = _temp.Combine("paths.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "settings": {
            "voice": {
              "enabled": false,
              "referenceAudioPath": "Z:\\missing\\reference.wav",
              "gptModelPath": "Z:\\missing\\gpt.ckpt",
              "sovitsModelPath": "Z:\\missing\\sovits.pth"
            }
          }
        }
        """);

        var result = await portability.ImportAsync(path);

        Assert.Equal(3, result.Warnings.Count);
        Assert.Equal("Z:\\missing\\reference.wav", service.Current.Voice.ReferenceAudioPath);
    }

    public void Dispose() => _temp.Dispose();
}
