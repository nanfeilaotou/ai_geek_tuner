using System.IO;
using System.Security;
using System.Text.Json;
using AIGeekTuner.Configuration;
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

        public JsonApplicationSettingsService(string? settingsPath = null)
        {
            var paths = ApplicationDataPaths.Default;
            _settingsPath = Path.GetFullPath(
                settingsPath ?? paths.SettingsFilePath);
            Current = settingsPath is null
                ? LoadWithLegacyFallback(
                    _settingsPath,
                    paths.LegacySettingsFilePath)
                : LoadOrDefault(_settingsPath);
        }

        public JsonApplicationSettingsService(ApplicationDataPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _settingsPath = paths.SettingsFilePath;
            Current = LoadWithLegacyFallback(
                _settingsPath,
                paths.LegacySettingsFilePath);
        }

        public ApplicationSettings Current { get; }

        public async Task SetAutoSaveDiagnosisHistoryAsync(
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new IOException("设置文件路径缺少有效目录。");
                }

                Directory.CreateDirectory(directory);
                var updatedSettings = new ApplicationSettings
                {
                    AutoSaveDiagnosisHistory = enabled
                };
                await WriteAtomicallyAsync(
                    updatedSettings,
                    cancellationToken);

                Current.AutoSaveDiagnosisHistory = enabled;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or SecurityException
                    or JsonException
                    or NotSupportedException
                    or ArgumentException)
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
                // A failed compatibility copy must not prevent startup.
            }

            return File.Exists(legacySettingsPath)
                ? LoadOrDefault(legacySettingsPath)
                : new ApplicationSettings();
        }

        private static ApplicationSettings LoadOrDefault(string settingsPath)
        {
            try
            {
                if (!File.Exists(settingsPath))
                {
                    return new ApplicationSettings();
                }

                var json = File.ReadAllText(settingsPath);
                return JsonSerializer.Deserialize<ApplicationSettings>(
                           json,
                           JsonOptions)
                       ?? new ApplicationSettings();
            }
            catch
            {
                // 无法读取或解析时使用安全默认值，不阻止程序启动。
                return new ApplicationSettings();
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
