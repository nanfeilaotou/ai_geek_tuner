using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.AI.Providers.Configuration
{
    /// <summary>
    /// Provider 配置存储：不可变快照 + 原子保存 + 损坏恢复。
    /// 首次加载时如果配置文件还不存在，会从旧版 Ollama 设置
    /// （BaseUrl / ModelName）迁移出一个等价的 ollama profile——
    /// 纯 additive：旧 settings.json 一个字节都不动，
    /// 现有 Diagnosis/SessionAnalysis 继续读取旧配置，完全不受影响。
    /// </summary>
    public interface IAiProviderProfileStore
    {
        AiProviderConfiguration Snapshot();

        /// <summary>校验 + 原子写入；磁盘写成功后才切换内存快照。失败抛出 <see cref="AiProviderStoreException"/>。</summary>
        Task SaveAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken = default);
    }

    public sealed class AiProviderStoreException : Exception
    {
        public AiProviderStoreException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    public sealed class AiProviderProfileStore : IAiProviderProfileStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private AiProviderConfiguration _current;

        public AiProviderProfileStore(
            string? filePath = null,
            OllamaOptions? legacyOllamaOptions = null)
        {
            _filePath = Path.GetFullPath(filePath
                ?? ApplicationDataPaths.Default.AiProvidersFilePath);
            _current = LoadInitialSnapshot(_filePath, legacyOllamaOptions);
        }

        public AiProviderConfiguration Snapshot() => Volatile.Read(ref _current);

        public async Task SaveAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var errors = ValidateConfiguration(configuration);
            if (errors.Count > 0)
            {
                throw new AiProviderStoreException(string.Join(" ", errors));
            }

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await WriteAtomicallyAsync(configuration, cancellationToken);

                // 磁盘写成功后才整体换入新快照；任何失败都不动内存。
                Volatile.Write(ref _current, configuration);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>配置文件级校验：Id 规范、profile 合法、Id 全局唯一。</summary>
        public static IReadOnlyList<string> ValidateConfiguration(AiProviderConfiguration configuration)
        {
            var errors = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in configuration.Profiles)
            {
                errors.AddRange(AiProviderProfileValidator.Validate(profile));
                if (!seenIds.Add(profile.Id))
                {
                    errors.Add($"存在重复的 Provider ID：{profile.Id}");
                }
            }

            return errors;
        }

        private static AiProviderConfiguration LoadInitialSnapshot(
            string filePath,
            OllamaOptions? legacyOllamaOptions)
        {
            if (!File.Exists(filePath))
            {
                return AiProviderConfigurationMigrator.MigrateFromLegacy(legacyOllamaOptions);
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var configuration = JsonSerializer.Deserialize<AiProviderConfiguration>(json, JsonOptions);
                if (configuration is null)
                {
                    return AiProviderConfiguration.Empty;
                }

                return SanitizeLoaded(configuration);
            }
            catch (JsonException exception)
            {
                // 损坏的 provider 配置不允许拖垮应用：
                // 留痕 → best-effort 改名备份 → 以（迁移出的）默认快照继续。
                ExceptionLogWriter.Write(exception, "AI providers load");
                BackupCorruptFile(filePath);
                return AiProviderConfigurationMigrator.MigrateFromLegacy(legacyOllamaOptions);
            }
            catch
            {
                // 读取类 IO 失败同样使用默认快照。
                return AiProviderConfigurationMigrator.MigrateFromLegacy(legacyOllamaOptions);
            }
        }

        /// <summary>加载后去除明显不合法的条目（重复 Id 保留第一个），不让单条坏数据毁掉整个文档。</summary>
        private static AiProviderConfiguration SanitizeLoaded(AiProviderConfiguration configuration)
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var validProfiles = new List<AiProviderProfile>();
            foreach (var profile in configuration.Profiles)
            {
                if (AiProviderProfileValidator.Validate(profile).Count == 0
                    && seenIds.Add(profile.Id))
                {
                    validProfiles.Add(profile);
                }
                else
                {
                    ExceptionLogWriter.Write(
                        new InvalidOperationException($"忽略不合法或重复的 Provider 配置条目：{profile.Id}"),
                        "AI providers sanitize");
                }
            }

            if (validProfiles.Count == configuration.Profiles.Count)
            {
                return configuration;
            }

            return new AiProviderConfiguration
            {
                Version = configuration.Version,
                Profiles = validProfiles
            };
        }

        private static void BackupCorruptFile(string filePath)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(directory) || !File.Exists(filePath))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(
                    filePath,
                    Path.Combine(directory, $"ai-providers.corrupt-{stamp}.json"),
                    overwrite: false);
            }
            catch (Exception backupFailure)
            {
                ExceptionLogWriter.Write(backupFailure, "AI providers corrupt backup");
            }
        }

        private async Task WriteAtomicallyAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new AiProviderStoreException("Provider 配置文件路径缺少有效目录。");
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4 * 1024,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or SecurityException
                    or NotSupportedException)
            {
                throw new AiProviderStoreException(
                    "无法保存 Provider 配置，请检查本地应用数据目录的访问权限。",
                    exception);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
