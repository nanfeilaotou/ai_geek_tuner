using System.IO;
using System.Reflection;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Settings;

public sealed record ApplicationSettingsExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string ApplicationVersion,
    ApplicationSettings Settings);

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
/// intentionally outside this document.
/// </summary>
public sealed class ApplicationSettingsPortabilityService
{
    public const int CurrentSchemaVersion = 1;

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
                ApplicationVersion(),
                Normalize(_settingsService.Current));
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
            var document = JsonSerializer.Deserialize<ApplicationSettingsExportDocument>(json, JsonOptions)
                ?? throw new SettingsPortabilityException("设置备份文件为空。");

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new SettingsPortabilityException(
                    $"不支持的应用设置版本：{document.SchemaVersion}。当前支持版本 {CurrentSchemaVersion}。" );
            }

            if (document.Settings is null)
            {
                throw new SettingsPortabilityException("设置备份缺少 settings 内容。" );
            }

            var settings = Normalize(document.Settings);
            var errors = ApplicationSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new SettingsPortabilityException("设置备份未通过校验：" + string.Join(" ", errors));
            }

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

    private static ApplicationSettings Normalize(ApplicationSettings settings) => new()
    {
        AutoSaveDiagnosisHistory = settings.AutoSaveDiagnosisHistory,
        OllamaBaseUrl = settings.OllamaBaseUrl ?? OllamaOptions.DefaultBaseUrl,
        OllamaModelName = settings.OllamaModelName ?? OllamaOptions.DefaultModelName,
        OllamaTimeoutSeconds = settings.OllamaTimeoutSeconds,
        MaxFaultLogCharacters = settings.MaxFaultLogCharacters,
        UseJsonFormat = settings.UseJsonFormat,
        RecordingIntervalMs = settings.RecordingIntervalMs,
        Voice = settings.Voice is null ? new VoiceSettings() : Normalize(settings.Voice),
        HardwareAutoRefresh = settings.HardwareAutoRefresh,
        HardwareRefreshIntervalMs = settings.HardwareRefreshIntervalMs
    };

    private static VoiceSettings Normalize(VoiceSettings voice) => new()
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

    private static string ApplicationVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
}
