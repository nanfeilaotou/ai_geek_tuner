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
    public async Task ExportImport_RoundTripsV2SettingsWithoutLegacyOllamaFields()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        var original = new ApplicationSettings
        {
            AutoSaveDiagnosisHistory = false,
            OllamaBaseUrl = "http://legacy.example.invalid:11434",
            OllamaModelName = "legacy-model",
            OllamaTimeoutSeconds = 45,
            MaxFaultLogCharacters = 20_000,
            UseJsonFormat = false,
            RecordingIntervalMs = 5000,
            HardwareAutoRefresh = false,
            HardwareRefreshIntervalMs = 1000,
            Voice = new VoiceSettings
            {
                Enabled = true,
                Endpoint = "http://127.0.0.1:9880",
                ReferenceAudioPath = _temp.Combine("ref.wav"),
                PromptText = "参考文本",
                PromptLang = "zh",
                SpeedFactor = 1.1,
                GptModelPath = _temp.Combine("gpt.ckpt"),
                SovitsModelPath = _temp.Combine("sovits.pth")
            }
        };
        await service.SaveAsync(original);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var exportPath = _temp.Combine("AIGeekTuner_Settings_v2.json");

        await portability.ExportAsync(exportPath);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2.0.0", root.GetProperty("applicationVersion").GetString());
        var settings = root.GetProperty("settings");
        Assert.Equal(45, settings.GetProperty("aiTimeoutSeconds").GetInt32());
        Assert.False(settings.GetProperty("autoSaveDiagnosisHistory").GetBoolean());
        Assert.False(settings.GetProperty("hardwareAutoRefresh").GetBoolean());
        Assert.Equal(1000, settings.GetProperty("hardwareRefreshIntervalMs").GetInt32());
        Assert.True(settings.GetProperty("voice").GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("ollamaBaseUrl", root.GetRawText());
        Assert.DoesNotContain("ollamaModelName", root.GetRawText());
        Assert.DoesNotContain("ollamaTimeoutSeconds", root.GetRawText());
        Assert.DoesNotContain("useJsonFormat", root.GetRawText());
        Assert.DoesNotContain("legacy-model", root.GetRawText());

        await service.SaveAsync(new ApplicationSettings());
        var imported = await portability.ImportAsync(exportPath);

        Assert.False(imported.Settings.AutoSaveDiagnosisHistory);
        Assert.Equal(45, service.Current.OllamaTimeoutSeconds);
        Assert.Equal(20_000, service.Current.MaxFaultLogCharacters);
        Assert.Equal(5000, service.Current.RecordingIntervalMs);
        Assert.False(service.Current.HardwareAutoRefresh);
        Assert.Equal(1000, service.Current.HardwareRefreshIntervalMs);
        Assert.Equal("参考文本", service.Current.Voice.PromptText);
        Assert.True(File.Exists(paths.SettingsFilePath + ".bak"));
    }

    [Fact]
    public async Task Import_V1LegacyBackupStillMapsOllamaTimeoutAndSettings()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var path = _temp.Combine("legacy-settings.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "exportedAtUtc": "2026-09-06T00:00:00Z",
          "applicationVersion": "1.0.0.0",
          "settings": {
            "autoSaveDiagnosisHistory": false,
            "ollamaBaseUrl": "http://192.168.1.50:11434",
            "ollamaModelName": "llama3.1:8b",
            "ollamaTimeoutSeconds": 45,
            "maxFaultLogCharacters": 8000,
            "useJsonFormat": false,
            "recordingIntervalMs": 5000,
            "hardwareAutoRefresh": false,
            "hardwareRefreshIntervalMs": 1000,
            "voice": { "enabled": false }
          }
        }
        """);

        var result = await portability.ImportAsync(path);

        Assert.False(result.Settings.AutoSaveDiagnosisHistory);
        Assert.Equal("http://192.168.1.50:11434", service.Current.OllamaBaseUrl);
        Assert.Equal("llama3.1:8b", service.Current.OllamaModelName);
        Assert.Equal(45, service.Current.OllamaTimeoutSeconds);
        Assert.False(service.Current.UseJsonFormat);
        Assert.Equal(5000, service.Current.RecordingIntervalMs);
        Assert.False(service.Current.HardwareAutoRefresh);
    }

    [Fact]
    public async Task Import_V2UnknownFieldsIsForwardTolerant()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var path = _temp.Combine("future-settings.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 2,
          "exportedAtUtc": "2026-09-06T00:00:00Z",
          "applicationVersion": "future",
          "settings": {
            "autoSaveDiagnosisHistory": false,
            "aiTimeoutSeconds": 60,
            "maxFaultLogCharacters": 12000,
            "recordingIntervalMs": 2000,
            "voice": { "enabled": false },
            "futurePreference": true
          }
        }
        """);

        var result = await portability.ImportAsync(path);

        Assert.False(result.Settings.AutoSaveDiagnosisHistory);
        Assert.Equal(60, service.Current.OllamaTimeoutSeconds);
    }

    [Fact]
    public async Task Import_InvalidV2IsAtomicAndLeavesCurrentUntouched()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var service = new JsonApplicationSettingsService(paths);
        await service.SaveAsync(new ApplicationSettings { OllamaTimeoutSeconds = 60 });
        var before = await File.ReadAllTextAsync(paths.SettingsFilePath);
        var portability = new ApplicationSettingsPortabilityService(service, paths.SettingsFilePath);
        var invalid = _temp.Combine("invalid-v2.json");
        await File.WriteAllTextAsync(invalid, """
        {
          "schemaVersion": 2,
          "settings": {
            "aiTimeoutSeconds": 1,
            "maxFaultLogCharacters": 12000,
            "recordingIntervalMs": 2000,
            "voice": { "enabled": false }
          }
        }
        """);

        await Assert.ThrowsAsync<SettingsPortabilityException>(
            () => portability.ImportAsync(invalid));

        Assert.Equal(60, service.Current.OllamaTimeoutSeconds);
        Assert.Equal(before, await File.ReadAllTextAsync(paths.SettingsFilePath));
        Assert.False(File.Exists(paths.SettingsFilePath + ".bak"));
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
          "schemaVersion": 2,
          "settings": {
            "aiTimeoutSeconds": 120,
            "maxFaultLogCharacters": 12000,
            "recordingIntervalMs": 2000,
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
