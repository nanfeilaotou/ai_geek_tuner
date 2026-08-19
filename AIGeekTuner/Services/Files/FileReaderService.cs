using System.IO;
using System.Text;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Files
{
    public sealed class FileReaderService : IFileReaderService
    {
        private static readonly HashSet<string> SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".txt", ".log" };

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly Encoding StrictUtf16LittleEndian =
            new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

        private static readonly Encoding StrictUtf16BigEndian =
            new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

        private readonly FileReaderOptions _options;

        public FileReaderService(FileReaderOptions? options = null)
        {
            _options = options ?? new FileReaderOptions();

            if (_options.MaxFileSizeBytes <= 0 ||
                _options.MaxFileSizeBytes > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "MaxFileSizeBytes 必须大于 0 且不能超过 Int32.MaxValue。");
            }
        }

        public async Task<FaultLog> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = ValidateAndNormalizePath(path);
            ValidateExtension(fullPath);

            byte[] bytes;
            try
            {
                bytes = await ReadFileBytesAsync(fullPath, cancellationToken);
            }
            catch (FileNotFoundException exception)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.FileNotFound,
                    $"日志文件不存在：{fullPath}",
                    exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.FileNotFound,
                    $"日志文件所在目录不存在：{fullPath}",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.AccessDenied,
                    $"没有权限读取日志文件：{fullPath}",
                    exception);
            }
            catch (IOException exception)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.IoFailure,
                    $"读取日志文件失败：{exception.Message}",
                    exception);
            }

            if (bytes.Length == 0)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.EmptyFile,
                    "日志文件为空。");
            }

            var decoded = Decode(bytes);
            if (string.IsNullOrWhiteSpace(decoded.Content))
            {
                throw new FaultLogReadException(
                    FaultLogReadError.EmptyFile,
                    "日志文件不包含有效文本内容。");
            }

            return new FaultLog
            {
                FileName = Path.GetFileName(fullPath),
                Content = decoded.Content,
                FileSizeBytes = bytes.LongLength,
                CreatedAt = TryGetCreationTime(fullPath),
                SourceType = FaultLogSourceType.File,
                EncodingName = decoded.EncodingName
            };
        }

        private async Task<byte[]> ReadFileBytesAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (stream.Length > _options.MaxFileSizeBytes)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.FileTooLarge,
                    $"日志文件大小为 {stream.Length} 字节，超过允许的 {_options.MaxFileSizeBytes} 字节。 ");
            }

            var buffer = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            var totalRead = 0;

            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead == buffer.Length)
            {
                return buffer;
            }

            return buffer[..totalRead];
        }

        private static DecodedLog Decode(byte[] bytes)
        {
            try
            {
                if (HasPrefix(bytes, 0xEF, 0xBB, 0xBF))
                {
                    return new DecodedLog(
                        StrictUtf8.GetString(bytes, 3, bytes.Length - 3),
                        "UTF-8");
                }

                if (HasPrefix(bytes, 0xFF, 0xFE))
                {
                    return new DecodedLog(
                        StrictUtf16LittleEndian.GetString(bytes, 2, bytes.Length - 2),
                        "UTF-16 LE");
                }

                if (HasPrefix(bytes, 0xFE, 0xFF))
                {
                    return new DecodedLog(
                        StrictUtf16BigEndian.GetString(bytes, 2, bytes.Length - 2),
                        "UTF-16 BE");
                }

                try
                {
                    return new DecodedLog(StrictUtf8.GetString(bytes), "UTF-8");
                }
                catch (DecoderFallbackException)
                {
                    var utf16Encoding = DetectBomlessUtf16(bytes);
                    if (utf16Encoding is not null)
                    {
                        return new DecodedLog(
                            utf16Encoding.GetString(bytes),
                            ReferenceEquals(utf16Encoding, StrictUtf16LittleEndian)
                                ? "UTF-16 LE"
                                : "UTF-16 BE");
                    }

                    throw;
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.UnsupportedEncoding,
                    "无法将日志解码为 UTF-8 或 UTF-16 文本。",
                    exception);
            }
        }

        private static Encoding? DetectBomlessUtf16(byte[] bytes)
        {
            if (bytes.Length < 2 || bytes.Length % 2 != 0)
            {
                return null;
            }

            var sampleLength = Math.Min(bytes.Length, 4096);
            var evenZeroCount = 0;
            var oddZeroCount = 0;

            for (var index = 0; index < sampleLength; index++)
            {
                if (bytes[index] != 0)
                {
                    continue;
                }

                if (index % 2 == 0)
                {
                    evenZeroCount++;
                }
                else
                {
                    oddZeroCount++;
                }
            }

            var minimumZeroCount = Math.Max(1, sampleLength / 8);
            if (oddZeroCount >= minimumZeroCount && oddZeroCount > evenZeroCount * 2)
            {
                return StrictUtf16LittleEndian;
            }

            if (evenZeroCount >= minimumZeroCount && evenZeroCount > oddZeroCount * 2)
            {
                return StrictUtf16BigEndian;
            }

            return null;
        }

        private static bool HasPrefix(byte[] bytes, params byte[] prefix)
        {
            if (bytes.Length < prefix.Length)
            {
                return false;
            }

            for (var index = 0; index < prefix.Length; index++)
            {
                if (bytes[index] != prefix[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static string ValidateAndNormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FaultLogReadException(
                    FaultLogReadError.InvalidPath,
                    "日志文件路径不能为空。");
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new FaultLogReadException(
                    FaultLogReadError.InvalidPath,
                    $"日志文件路径无效：{path}",
                    exception);
            }
        }

        private static void ValidateExtension(string fullPath)
        {
            var extension = Path.GetExtension(fullPath);
            if (!SupportedExtensions.Contains(extension))
            {
                throw new FaultLogReadException(
                    FaultLogReadError.UnsupportedFileType,
                    $"不支持文件类型“{extension}”，只允许 .txt 和 .log。");
            }
        }

        private static DateTimeOffset? TryGetCreationTime(string fullPath)
        {
            try
            {
                var createdAt = File.GetCreationTimeUtc(fullPath);
                return createdAt.Year <= 1601
                    ? null
                    : new DateTimeOffset(createdAt, TimeSpan.Zero);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private sealed record DecodedLog(string Content, string EncodingName);
    }
}
