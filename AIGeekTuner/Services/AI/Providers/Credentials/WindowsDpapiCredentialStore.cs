using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.AI.Providers.Credentials
{
    /// <summary>
    /// 基于 Windows DPAPI（CurrentUser）的凭据存储：
    /// 明文只在 protect/unprotect 的调用栈里存在，磁盘上只有 DPAPI blob。
    /// 写入走 tmp+move 原子替换；损坏的 blob / 文件按“不可用”处理并留痕，不影响启动。
    /// </summary>
    public sealed class WindowsDpapiCredentialStore : IAiCredentialStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public WindowsDpapiCredentialStore(string? filePath = null)
        {
            _filePath = Path.GetFullPath(filePath
                ?? ApplicationDataPaths.Default.AiCredentialsFilePath);
        }

        public string FilePath => _filePath;

        public async Task SaveAsync(string providerId, string plainTextSecret, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                throw new ArgumentException("providerId 不能为空。", nameof(providerId));
            }

            if (string.IsNullOrEmpty(plainTextSecret))
            {
                throw new ArgumentException("密钥内容不能为空。", nameof(plainTextSecret));
            }

            var protectedBlob = Convert.ToBase64String(
                ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plainTextSecret),
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser));

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                var document = await LoadDocumentAsync(cancellationToken);
                var entry = document.Credentials.FirstOrDefault(entry =>
                    string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal));
                if (entry is null)
                {
                    document.Credentials.Add(new AiCredentialFileEntry
                    {
                        ProviderId = providerId,
                        ProtectedBlobBase64 = protectedBlob
                    });
                }
                else
                {
                    entry.ProtectedBlobBase64 = protectedBlob;
                }

                await WriteAtomicallyAsync(document, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            var document = await LoadDocumentAsync(cancellationToken);
            var entry = document.Credentials.FirstOrDefault(entry =>
                string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal));
            if (entry is null || string.IsNullOrWhiteSpace(entry.ProtectedBlobBase64))
            {
                return null;
            }

            try
            {
                var blob = Convert.FromBase64String(entry.ProtectedBlobBase64);
                var plain = ProtectedData.Unprotect(
                    blob,
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception exception) when (
                exception is FormatException
                    or CryptographicException)
            {
                // 损坏的 blob：留痕并视为未配置——绝不让读取路径变成崩溃点，
                // 也绝不把 blob 或异常细节（可能含密钥信息）写进日志。
                ExceptionLogWriter.Write(
                    new InvalidOperationException("凭据 blob 无法解密，已视为未配置。", exception),
                    "AI credential load");
                return null;
            }
        }

        public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return;
            }

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                var document = await LoadDocumentAsync(cancellationToken);
                var removed = document.Credentials.RemoveAll(entry =>
                    string.Equals(entry.ProviderId, providerId, StringComparison.Ordinal));
                if (removed == 0)
                {
                    return;
                }

                await WriteAtomicallyAsync(document, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>读取文档；missing → 空文档；损坏 → 留痕 + best-effort 改名备份 + 空文档。</summary>
        private async Task<AiCredentialFileDocument> LoadDocumentAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_filePath))
            {
                return new AiCredentialFileDocument();
            }

            try
            {
                var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
                return JsonSerializer.Deserialize<AiCredentialFileDocument>(json, JsonOptions)
                    ?? new AiCredentialFileDocument();
            }
            catch (JsonException exception)
            {
                ExceptionLogWriter.Write(exception, "AI credentials load");
                BackupCorruptFile();
                return new AiCredentialFileDocument();
            }
            catch
            {
                // 读取类 IO 失败同样按空文档处理，但无内容可备份。
                return new AiCredentialFileDocument();
            }
        }

        private void BackupCorruptFile()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_filePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(
                    _filePath,
                    Path.Combine(directory, $"credentials.corrupt-{stamp}.json"),
                    overwrite: false);
            }
            catch (Exception backupFailure)
            {
                ExceptionLogWriter.Write(backupFailure, "AI credentials corrupt backup");
            }
        }

        private async Task WriteAtomicallyAsync(AiCredentialFileDocument document, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException("凭据文件路径缺少有效目录。");
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
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
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
                throw new IOException("无法保存 AI 凭据文件，请检查本地应用数据目录的访问权限。", exception);
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
