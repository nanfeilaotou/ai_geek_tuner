using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Settings
{
    public sealed class JsonApplicationSettingsService : IApplicationSettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly string _settingsPath;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private ApplicationSettings _current;

        public JsonApplicationSettingsService(string? settingsPath = null)
        {
            var paths = ApplicationDataPaths.Default;
            _settingsPath = Path.GetFullPath(
                settingsPath ?? paths.SettingsFilePath);
            _current = settingsPath is null
                ? LoadWithLegacyFallback(
                    _settingsPath,
                    paths.LegacySettingsFilePath)
                : LoadOrDefault(_settingsPath);
        }

        public JsonApplicationSettingsService(ApplicationDataPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _settingsPath = paths.SettingsFilePath;
            _current = LoadWithLegacyFallback(
                _settingsPath,
                paths.LegacySettingsFilePath);
        }

        public ApplicationSettings Current => Volatile.Read(ref _current);

        public async Task SaveAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var errors = ApplicationSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new ApplicationSettingsException(string.Join(" ", errors));
            }

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new IOException("设置文件路径缺少有效目录。");
                }

                Directory.CreateDirectory(directory);
                await WriteAtomicallyAsync(settings, cancellationToken);

                // 磁盘写成功后才切换内存快照，保证两者不出现“先失效后失败”的错位。
                Volatile.Write(ref _current, settings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or SecurityException
                    or NotSupportedException)
            {
                throw new ApplicationSettingsException(
                    "无法保存应用设置，请检查本地应用数据目录的访问权限。",
                    exception);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static ApplicationSettings LoadWithLegacyFallback(
            string settingsPath,
            string legacySettingsPath)
        {
            if (File.Exists(settingsPath))
            {
                return LoadOrDefault(settingsPath);
            }

            try
            {
                if (!File.Exists(legacySettingsPath))
                {
                    return new ApplicationSettings();
                }

                var directory = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    File.Copy(
                        legacySettingsPath,
                        settingsPath,
                        overwrite: false);
                    return LoadOrDefault(settingsPath);
                }
            }
            catch
            {
                // 兼容迁移失败不能阻止启动。
            }

            return File.Exists(legacySettingsPath)
                ? LoadOrDefault(legacySettingsPath)
                : new ApplicationSettings();
        }

        private static ApplicationSettings LoadOrDefault(string settingsPath)
        {
            if (!File.Exists(settingsPath))
            {
                return new ApplicationSettings();
            }

            try
            {
                var json = File.ReadAllText(settingsPath);
                return JsonSerializer.Deserialize<ApplicationSettings>(
                           json,
                           JsonOptions)
                       ?? new ApplicationSettings();
            }
            catch (JsonException exception)
            {
                // 损坏的 settings.json 不允许拖垮应用：
                // 留痕 → best-effort 改名备份 → 以默认配置继续启动。
                ExceptionLogWriter.Write(exception, "Settings load");
                BackupCorruptSettings(settingsPath);
                return new ApplicationSettings();
            }
            catch
            {
                // 读取类 IO 失败同样使用默认值，但无内容可备份。
                return new ApplicationSettings();
            }
        }

        private static void BackupCorruptSettings(string settingsPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(settingsPath);
                if (string.IsNullOrWhiteSpace(directory) || !File.Exists(settingsPath))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(
                    settingsPath,
                    Path.Combine(directory, $"settings.corrupt-{stamp}.json"),
                    overwrite: false);
            }
            catch (Exception backupFailure)
            {
                ExceptionLogWriter.Write(backupFailure, "Settings corrupt backup");
            }
        }

        private async Task WriteAtomicallyAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken)
        {
            var temporaryPath = _settingsPath + ".tmp";
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
                    await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        JsonOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, _settingsPath, overwrite: true);
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
