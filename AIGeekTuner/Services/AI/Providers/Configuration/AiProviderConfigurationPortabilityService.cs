using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.AI.Providers.Configuration;

public sealed record AiProviderExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string ApplicationVersion,
    bool CredentialsIncluded,
    string? ActiveProviderId,
    IReadOnlyList<AiProviderProfile> Profiles);

public sealed record AiProviderImportResult(
    string? ActiveProviderId,
    IReadOnlyList<string> MissingCredentialProviderIds);

public sealed class AiProviderPortabilityException : Exception
{
    public AiProviderPortabilityException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Imports and exports only the non-sensitive provider catalog. The DPAPI
/// credential store is never read for export and never written for import.
/// </summary>
public sealed class AiProviderConfigurationPortabilityService
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAiProviderProfileStore _store;
    private readonly IAiCredentialStore _credentials;
    private readonly string _providersPath;

    public AiProviderConfigurationPortabilityService(
        IAiProviderProfileStore store,
        IAiCredentialStore credentials,
        string providersPath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _providersPath = Path.GetFullPath(providersPath ?? throw new ArgumentNullException(nameof(providersPath)));
    }

    public async Task<string> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = _store.Snapshot();
            var document = new AiProviderExportDocument(
                CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                ApplicationVersionInfo.Current,
                CredentialsIncluded: false,
                snapshot.ActiveProviderId,
                snapshot.Profiles.Select(Normalize).ToArray());
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
            throw new AiProviderPortabilityException("Provider 配置导出失败，请检查目标文件夹权限。", exception);
        }
    }

    public async Task<AiProviderImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
            var document = JsonSerializer.Deserialize<AiProviderExportDocument>(json, JsonOptions)
                ?? throw new AiProviderPortabilityException("Provider 备份文件为空。");

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new AiProviderPortabilityException(
                    $"不支持的 Provider 配置版本：{document.SchemaVersion}。当前支持版本 {CurrentSchemaVersion}。");
            }

            if (document.CredentialsIncluded)
            {
                throw new AiProviderPortabilityException(
                    "Provider 备份声明包含凭据；为安全起见拒绝导入。请导出不含 API Key 的配置文件。");
            }

            if (document.Profiles is null)
            {
                throw new AiProviderPortabilityException("Provider 备份缺少 profiles 内容。");
            }

            var importedProfiles = NormalizeAndValidate(document.Profiles);
            var current = _store.Snapshot();
            var merged = MergeProfiles(current.Profiles, importedProfiles);
            var activeProviderId = ResolveActiveProviderId(
                current.ActiveProviderId,
                document.ActiveProviderId,
                merged);
            var missingCredentials = await FindMissingCredentialsAsync(
                importedProfiles,
                cancellationToken);

            BackupCurrentCatalog();
            await _store.SaveAsync(
                new AiProviderConfiguration
                {
                    Version = current.Version,
                    Profiles = merged,
                    ActiveProviderId = activeProviderId
                },
                cancellationToken);

            return new AiProviderImportResult(
                activeProviderId,
                missingCredentials);
        }
        catch (AiProviderPortabilityException)
        {
            throw;
        }
        catch (AiProviderStoreException exception)
        {
            throw new AiProviderPortabilityException(exception.Message, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new AiProviderPortabilityException("Provider 备份不是有效的 JSON 文件。", exception);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException)
        {
            throw new AiProviderPortabilityException("Provider 配置导入失败，请检查文件和本地数据目录。", exception);
        }
    }

    private static IReadOnlyList<AiProviderProfile> NormalizeAndValidate(
        IReadOnlyList<AiProviderProfile> profiles)
    {
        var normalized = new List<AiProviderProfile>(profiles.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (profile is null)
            {
                throw new AiProviderPortabilityException("Provider 列表包含空条目。");
            }

            if (profile.Models is not null && profile.Models.Any(model => model is null))
            {
                throw new AiProviderPortabilityException($"Provider {profile.Id} 的模型列表包含空条目。");
            }

            var value = Normalize(profile);
            if (!ids.Add(value.Id))
            {
                throw new AiProviderPortabilityException($"Provider ID 重复：{value.Id}");
            }

            var errors = AiProviderProfileValidator.Validate(value);
            if (errors.Count > 0)
            {
                throw new AiProviderPortabilityException(
                    $"Provider {value.Id} 未通过校验：{string.Join(" ", errors)}");
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static IReadOnlyList<AiProviderProfile> MergeProfiles(
        IReadOnlyList<AiProviderProfile> current,
        IReadOnlyList<AiProviderProfile> imported)
    {
        var merged = current.ToList();
        foreach (var profile in imported)
        {
            var index = merged.FindIndex(candidate =>
                string.Equals(candidate.Id, profile.Id, StringComparison.Ordinal));
            if (index < 0 && merged.Any(candidate =>
                    string.Equals(candidate.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AiProviderPortabilityException(
                    $"Provider ID 仅大小写不同，可能导致本机凭据错配：{profile.Id}");
            }

            if (index >= 0)
            {
                merged[index] = profile;
            }
            else
            {
                merged.Add(profile);
            }
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (merged.Any(profile => !ids.Add(profile.Id)))
        {
            throw new AiProviderPortabilityException("合并后的 Provider ID 不唯一。" );
        }

        return merged;
    }

    private static string? ResolveActiveProviderId(
        string? currentActiveId,
        string? importedActiveId,
        IReadOnlyList<AiProviderProfile> merged)
    {
        if (string.IsNullOrWhiteSpace(importedActiveId))
        {
            return currentActiveId;
        }

        var candidate = merged.FirstOrDefault(profile =>
            string.Equals(profile.Id, importedActiveId, StringComparison.Ordinal));
        return candidate is not null && candidate.Enabled
            && !string.IsNullOrWhiteSpace(candidate.DefaultModelId)
            ? candidate.Id
            : currentActiveId;
    }

    private async Task<IReadOnlyList<string>> FindMissingCredentialsAsync(
        IReadOnlyList<AiProviderProfile> importedProfiles,
        CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        foreach (var profile in importedProfiles.Where(profile =>
                     profile.Kind == AiProviderKind.OpenAiCompatible))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(await _credentials.LoadAsync(profile.Id, cancellationToken)))
                {
                    missing.Add(profile.Id);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Credential availability is a non-blocking import warning.
                missing.Add(profile.Id);
            }
        }

        return missing;
    }

    private void BackupCurrentCatalog()
    {
        if (!File.Exists(_providersPath))
        {
            return;
        }

        try
        {
            File.Copy(_providersPath, _providersPath + ".bak", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException)
        {
            throw new AiProviderPortabilityException("无法创建 Provider 配置备份，导入已取消。", exception);
        }
    }

    private static AiProviderProfile Normalize(AiProviderProfile profile) => new()
    {
        Id = profile.Id ?? string.Empty,
        DisplayName = profile.DisplayName ?? string.Empty,
        Kind = profile.Kind,
        BaseUrl = profile.BaseUrl ?? string.Empty,
        Models = profile.Models is null
            ? Array.Empty<AiProviderModel>()
            : profile.Models.Where(model => model is not null).Select(Normalize).ToArray(),
        DefaultModelId = profile.DefaultModelId,
        StructuredOutputMode = profile.StructuredOutputMode,
        Enabled = profile.Enabled
    };

    private static AiProviderModel Normalize(AiProviderModel model) => new(
        model.Id ?? string.Empty,
        model.DisplayName);

}
