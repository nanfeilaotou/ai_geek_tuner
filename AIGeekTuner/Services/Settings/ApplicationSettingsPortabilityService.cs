using System.IO;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Settings;

/// <summary>
/// Deliberately narrow portable application-settings contract. Runtime
/// persistence still owns the legacy Ollama fields, but they are not part of
/// this public backup format.
/// </summary>
public sealed class PortableApplicationSettings
{
    public bool AutoSaveDiagnosisHistory { get; init; } = true;

    /// <summary>Global AI request timeout; the runtime property retains its old name for migration.</summary>
    public int AiTimeoutSeconds { get; init; } = OllamaOptions.DefaultTimeoutSeconds;

    public int MaxFaultLogCharacters { get; init; } =
        DiagnosisInputOptions.DefaultMaxFaultLogCharacters;

    public int RecordingIntervalMs { get; init; } = 2000;

    public PortableVoiceSettings Voice { get; init; } = new();

    public bool HardwareAutoRefresh { get; init; } = true;

    public int HardwareRefreshIntervalMs { get; init; } = 2000;
}

public sealed class PortableVoiceSettings
{
    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = "http://127.0.0.1:9880";

    public string ReferenceAudioPath { get; init; } = string.Empty;

    public string PromptText { get; init; } = string.Empty;

    public string PromptLang { get; init; } = "ja";

    public double SpeedFactor { get; init; } = 1.0;

    public string GptModelPath { get; init; } = string.Empty;

    public string SovitsModelPath { get; init; } = string.Empty;
}

public sealed record ApplicationSettingsExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string ApplicationVersion,
    PortableApplicationSettings Settings);

public sealed record ApplicationSettingsImportResult(
    ApplicationSettings Settings,
    IReadOnlyList<string> Warnings);

public sealed class SettingsPortabilityException : Exception
{
    public SettingsPortabilityException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Versioned settings backup/restore. Provider profiles and credentials are
/// intentionally outside this document. Export writes v2; import accepts v1
/// and v2 so existing user backups remain usable.
/// </summary>
public sealed class ApplicationSettingsPortabilityService
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IApplicationSettingsService _settingsService;
    private readonly string _settingsPath;

    public ApplicationSettingsPortabilityService(
        IApplicationSettingsService settingsService,
        string settingsPath)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settingsPath = Path.GetFullPath(settingsPath ?? throw new ArgumentNullException(nameof(settingsPath)));
    }

    public async Task<string> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var document = new ApplicationSettingsExportDocument(
                CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                ApplicationVersionInfo.Current,
                ToPortable(_settingsService.Current));
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await AtomicFileWriter.WriteTextAsync(destinationPath, json, cancellationToken);
            return Path.GetFullPath(destinationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException)
        {
            throw new SettingsPortabilityException("应用设置导出失败，请检查目标文件夹权限。", exception);
        }
    }

    public async Task<ApplicationSettingsImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
            var settings = DeserializeImportedSettings(json);
            var errors = ApplicationSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new SettingsPortabilityException("设置备份未通过校验：" + string.Join(" ", errors));
            }

            // Validate and normalize before this point so rejected v2 files do
            // not create a backup or change either the in-memory or disk state.
            BackupCurrentSettings();
            await _settingsService.SaveAsync(settings, cancellationToken);
            return new ApplicationSettingsImportResult(settings, MissingLocalPathWarnings(settings));
        }
        catch (SettingsPortabilityException)
        {
            throw;
        }
        catch (ApplicationSettingsException exception)
        {
            throw new SettingsPortabilityException(exception.Message, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SettingsPortabilityException("设置备份不是有效的 JSON 文件。", exception);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException)
        {
            throw new SettingsPortabilityException("应用设置导入失败，请检查文件和本地数据目录。", exception);
        }
    }

    private ApplicationSettings DeserializeImportedSettings(string json)
    {
        using var rootDocument = JsonDocument.Parse(json);
        var root = rootDocument.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schemaElement)
            || schemaElement.ValueKind != JsonValueKind.Number
            || !schemaElement.TryGetInt32(out var schemaVersion))
        {
            throw new SettingsPortabilityException("设置备份缺少有效的 schemaVersion。");
        }

        return schemaVersion switch
        {
            LegacySchemaVersion => DeserializeV1(root),
            CurrentSchemaVersion => DeserializeV2(root),
            _ => throw new SettingsPortabilityException(
                $"不支持的应用设置版本：{schemaVersion}。当前支持版本 {CurrentSchemaVersion}，并兼容版本 {LegacySchemaVersion}。")
        };
    }

    private static ApplicationSettings DeserializeV1(JsonElement root)
    {
        var document = root.Deserialize<LegacyApplicationSettingsExportDocument>(JsonOptions)
            ?? throw new SettingsPortabilityException("设置备份文件为空。");
        if (document.Settings is null)
        {
            throw new SettingsPortabilityException("设置备份缺少 settings 内容。");
        }

        // v1's Ollama fields are accepted only on the backward-import path.
        // This keeps old migration backups useful without leaking those fields
        // into newly generated v2 exports.
        return NormalizeLegacy(document.Settings);
    }

    private ApplicationSettings DeserializeV2(JsonElement root)
    {
        var document = root.Deserialize<ApplicationSettingsExportDocument>(JsonOptions)
            ?? throw new SettingsPortabilityException("设置备份文件为空。");
        if (document.Settings is null)
        {
            throw new SettingsPortabilityException("设置备份缺少 settings 内容。");
        }

        return FromPortable(document.Settings);
    }

    private void BackupCurrentSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            File.Copy(_settingsPath, _settingsPath + ".bak", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException)
        {
            throw new SettingsPortabilityException("无法创建应用设置备份，导入已取消。", exception);
        }
    }

    private PortableApplicationSettings ToPortable(ApplicationSettings settings) => new()
    {
        AutoSaveDiagnosisHistory = settings.AutoSaveDiagnosisHistory,
        AiTimeoutSeconds = settings.OllamaTimeoutSeconds,
        MaxFaultLogCharacters = settings.MaxFaultLogCharacters,
        RecordingIntervalMs = settings.RecordingIntervalMs,
        Voice = ToPortable(settings.Voice),
        HardwareAutoRefresh = settings.HardwareAutoRefresh,
        HardwareRefreshIntervalMs = settings.HardwareRefreshIntervalMs
    };

    private static PortableVoiceSettings ToPortable(VoiceSettings? voice)
    {
        voice ??= new VoiceSettings();
        return new PortableVoiceSettings
        {
            Enabled = voice.Enabled,
            Endpoint = voice.Endpoint ?? string.Empty,
            ReferenceAudioPath = voice.ReferenceAudioPath ?? string.Empty,
            PromptText = voice.PromptText ?? string.Empty,
            PromptLang = voice.PromptLang ?? string.Empty,
            SpeedFactor = voice.SpeedFactor,
            GptModelPath = voice.GptModelPath ?? string.Empty,
            SovitsModelPath = voice.SovitsModelPath ?? string.Empty
        };
    }

    private ApplicationSettings FromPortable(PortableApplicationSettings settings)
    {
        var current = _settingsService.Current;
        return new ApplicationSettings
        {
            AutoSaveDiagnosisHistory = settings.AutoSaveDiagnosisHistory,
            // Keep runtime persistence compatibility while the portable
            // contract uses the provider-neutral name aiTimeoutSeconds.
            OllamaBaseUrl = current.OllamaBaseUrl,
            OllamaModelName = current.OllamaModelName,
            OllamaTimeoutSeconds = settings.AiTimeoutSeconds,
            MaxFaultLogCharacters = settings.MaxFaultLogCharacters,
            UseJsonFormat = current.UseJsonFormat,
            RecordingIntervalMs = settings.RecordingIntervalMs,
            Voice = FromPortable(settings.Voice),
            HardwareAutoRefresh = settings.HardwareAutoRefresh,
            HardwareRefreshIntervalMs = settings.HardwareRefreshIntervalMs
        };
    }

    private static VoiceSettings FromPortable(PortableVoiceSettings? voice)
    {
        voice ??= new PortableVoiceSettings();
        return new VoiceSettings
        {
            Enabled = voice.Enabled,
            Endpoint = voice.Endpoint ?? string.Empty,
            ReferenceAudioPath = voice.ReferenceAudioPath ?? string.Empty,
            PromptText = voice.PromptText ?? string.Empty,
            PromptLang = voice.PromptLang ?? string.Empty,
            SpeedFactor = voice.SpeedFactor,
            GptModelPath = voice.GptModelPath ?? string.Empty,
            SovitsModelPath = voice.SovitsModelPath ?? string.Empty
        };
    }

    private static ApplicationSettings NormalizeLegacy(ApplicationSettings settings) => new()
    {
        AutoSaveDiagnosisHistory = settings.AutoSaveDiagnosisHistory,
        OllamaBaseUrl = settings.OllamaBaseUrl ?? OllamaOptions.DefaultBaseUrl,
        OllamaModelName = settings.OllamaModelName ?? OllamaOptions.DefaultModelName,
        OllamaTimeoutSeconds = settings.OllamaTimeoutSeconds,
        MaxFaultLogCharacters = settings.MaxFaultLogCharacters,
        UseJsonFormat = settings.UseJsonFormat,
        RecordingIntervalMs = settings.RecordingIntervalMs,
        Voice = settings.Voice is null ? new VoiceSettings() : NormalizeLegacy(settings.Voice),
        HardwareAutoRefresh = settings.HardwareAutoRefresh,
        HardwareRefreshIntervalMs = settings.HardwareRefreshIntervalMs
    };

    private static VoiceSettings NormalizeLegacy(VoiceSettings voice) => new()
    {
        Enabled = voice.Enabled,
        Endpoint = voice.Endpoint ?? string.Empty,
        ReferenceAudioPath = voice.ReferenceAudioPath ?? string.Empty,
        PromptText = voice.PromptText ?? string.Empty,
        PromptLang = voice.PromptLang ?? string.Empty,
        SpeedFactor = voice.SpeedFactor,
        GptModelPath = voice.GptModelPath ?? string.Empty,
        SovitsModelPath = voice.SovitsModelPath ?? string.Empty
    };

    private static IReadOnlyList<string> MissingLocalPathWarnings(ApplicationSettings settings)
    {
        var warnings = new List<string>();
        var paths = new[]
        {
            (Label: "参考音频", Path: settings.Voice.ReferenceAudioPath),
            (Label: "GPT 权重", Path: settings.Voice.GptModelPath),
            (Label: "SoVITS 权重", Path: settings.Voice.SovitsModelPath)
        };

        foreach (var item in paths)
        {
            if (!string.IsNullOrWhiteSpace(item.Path) && !File.Exists(item.Path))
            {
                warnings.Add($"{item.Label}路径不存在，请重新选择：{item.Path}");
            }
        }

        return warnings;
    }

    private sealed record LegacyApplicationSettingsExportDocument(
        int SchemaVersion,
        DateTimeOffset ExportedAtUtc,
        string ApplicationVersion,
        ApplicationSettings Settings);
}
