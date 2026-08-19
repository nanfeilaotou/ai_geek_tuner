using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.History
{
    public sealed class LocalDiagnosisHistoryService : IDiagnosisHistoryService
    {
        private const string IndexFileName = "records.json";
        private const string LegacyMigrationMarkerFileName =
            ".legacy-history-imported";
        private const string PastedLogName = "粘贴日志";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _reportsDirectory;
        private readonly string _indexPath;
        private readonly string _legacyMigrationMarkerPath;
        private readonly string? _legacyReportsDirectory;
        private readonly SemaphoreSlim _storageLock = new(1, 1);

        public LocalDiagnosisHistoryService(string? reportsDirectory = null)
        {
            var paths = ApplicationDataPaths.Default;
            _reportsDirectory = Path.GetFullPath(
                reportsDirectory ?? paths.HistoryDirectory);
            _indexPath = Path.Combine(_reportsDirectory, IndexFileName);
            _legacyMigrationMarkerPath = Path.Combine(
                _reportsDirectory,
                LegacyMigrationMarkerFileName);
            _legacyReportsDirectory = reportsDirectory is null
                ? paths.LegacyHistoryDirectory
                : null;
            TryImportLegacyHistoryOnce();
        }

        public LocalDiagnosisHistoryService(ApplicationDataPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _reportsDirectory = paths.HistoryDirectory;
            _indexPath = Path.Combine(_reportsDirectory, IndexFileName);
            _legacyMigrationMarkerPath = Path.Combine(
                _reportsDirectory,
                LegacyMigrationMarkerFileName);
            _legacyReportsDirectory = paths.LegacyHistoryDirectory;
            TryImportLegacyHistoryOnce();
        }

        public async Task<DiagnosisRecord> SaveAsync(
            DiagnosisOutcome outcome,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(outcome);

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_reportsDirectory);

                var outcomePath = Path.Combine(
                    _reportsDirectory,
                    $"{outcome.DiagnosisId:N}.json");
                var record = CreateRecord(outcome, outcomePath);

                await WriteJsonAtomicallyAsync(
                    outcomePath,
                    outcome,
                    cancellationToken);

                var records = await ReadRecordsCoreAsync(cancellationToken);
                records.RemoveAll(item => item.DiagnosisId == record.DiagnosisId);
                records.Add(record);
                records.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));

                await WriteJsonAtomicallyAsync(
                    _indexPath,
                    records,
                    cancellationToken);

                return record;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new DiagnosisHistoryException(
                    "诊断已经完成，但本地历史记录保存失败。请检查应用数据目录的访问权限。",
                    exception);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        public async Task<IReadOnlyList<DiagnosisRecord>> GetRecordsAsync(
            CancellationToken cancellationToken)
        {
            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                var records = await ReadRecordsCoreAsync(cancellationToken);
                return records
                    .OrderByDescending(item => item.CreatedAt)
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new DiagnosisHistoryException(
                    "无法读取本地诊断历史记录。历史索引可能已损坏或当前目录不可访问。",
                    exception);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        public async Task<DiagnosisOutcome> LoadOutcomeAsync(
            DiagnosisRecord record,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                var outcomePath = ValidateOutcomePath(record);
                if (!File.Exists(outcomePath))
                {
                    throw new FileNotFoundException(
                        "诊断报告文件不存在。",
                        outcomePath);
                }

                await using var stream = new FileStream(
                    outcomePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    useAsync: true);
                var outcome = await JsonSerializer.DeserializeAsync<DiagnosisOutcome>(
                    stream,
                    JsonOptions,
                    cancellationToken);

                if (outcome is null || outcome.DiagnosisId != record.DiagnosisId)
                {
                    throw new JsonException("诊断报告内容无效或与历史记录不匹配。");
                }

                return outcome;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new DiagnosisHistoryException(
                    "无法打开该诊断报告。报告文件可能已移动、删除或损坏。",
                    exception);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        public async Task DeleteAsync(
            Guid diagnosisId,
            CancellationToken cancellationToken = default)
        {
            if (diagnosisId == Guid.Empty)
            {
                throw new ArgumentException(
                    "DiagnosisId 不能为空。",
                    nameof(diagnosisId));
            }

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                var records = await ReadRecordsCoreAsync(cancellationToken);
                var removedCount = records.RemoveAll(
                    item => item.DiagnosisId == diagnosisId);

                var outcomePath = Path.Combine(
                    _reportsDirectory,
                    $"{diagnosisId:N}.json");
                if (File.Exists(outcomePath))
                {
                    File.Delete(outcomePath);
                }

                if (removedCount > 0)
                {
                    Directory.CreateDirectory(_reportsDirectory);
                    await WriteJsonAtomicallyAsync(
                        _indexPath,
                        records,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new DiagnosisHistoryException(
                    "无法删除该诊断记录，请检查本地报告目录的访问权限。",
                    exception);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        private static DiagnosisRecord CreateRecord(
            DiagnosisOutcome outcome,
            string outcomePath) => new()
        {
            DiagnosisId = outcome.DiagnosisId,
            CreatedAt = outcome.CompletedAt.ToUniversalTime(),
            LogFileName = string.IsNullOrWhiteSpace(outcome.Request.FaultLog.FileName)
                ? PastedLogName
                : outcome.Request.FaultLog.FileName,
            Summary = outcome.AiResult.Summary,
            RiskLevel = outcome.AiResult.RiskLevel,
            Confidence = Math.Clamp(outcome.AiResult.Confidence, 0, 1),
            SafetyStatus = outcome.Safety.Status,
            DiagnosisOutcomePath = outcomePath
        };

        private async Task<List<DiagnosisRecord>> ReadRecordsCoreAsync(
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_indexPath))
            {
                return [];
            }

            await using var stream = new FileStream(
                _indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<List<DiagnosisRecord>>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? [];
        }

        private string ValidateOutcomePath(DiagnosisRecord record)
        {
            var expectedPath = Path.GetFullPath(Path.Combine(
                _reportsDirectory,
                $"{record.DiagnosisId:N}.json"));
            var storedPath = Path.GetFullPath(record.DiagnosisOutcomePath);

            if (!string.Equals(
                    expectedPath,
                    storedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("历史记录包含无效的诊断报告路径。");
            }

            return expectedPath;
        }

        private static async Task WriteJsonAtomicallyAsync<T>(
            string destinationPath,
            T value,
            CancellationToken cancellationToken)
        {
            var temporaryPath = destinationPath + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 16 * 1024,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        value,
                        JsonOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static bool IsStorageException(Exception exception) =>
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException
                or ArgumentException
                or SecurityException;

        private void TryImportLegacyHistoryOnce()
        {
            if (File.Exists(_legacyMigrationMarkerPath))
            {
                return;
            }

            // An existing LocalAppData index means this installation has
            // already initialized or migrated the new History directory.
            // Mark it complete without consulting the legacy directory again,
            // otherwise deleted records can be resurrected on the next start.
            if (File.Exists(_indexPath))
            {
                TryMarkLegacyMigrationCompleted();
                return;
            }

            if (string.IsNullOrWhiteSpace(_legacyReportsDirectory)
                || string.Equals(
                    _reportsDirectory,
                    _legacyReportsDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(_legacyReportsDirectory))
            {
                TryMarkLegacyMigrationCompleted();
                return;
            }

            try
            {
                var legacyIndexPath = Path.Combine(
                    _legacyReportsDirectory,
                    IndexFileName);
                if (!File.Exists(legacyIndexPath))
                {
                    TryMarkLegacyMigrationCompleted();
                    return;
                }

                var legacyRecords =
                    JsonSerializer.Deserialize<List<DiagnosisRecord>>(
                        File.ReadAllText(legacyIndexPath),
                        JsonOptions)
                    ?? [];
                if (legacyRecords.Count == 0)
                {
                    TryMarkLegacyMigrationCompleted();
                    return;
                }

                Directory.CreateDirectory(_reportsDirectory);
                var currentRecords = File.Exists(_indexPath)
                    ? JsonSerializer.Deserialize<List<DiagnosisRecord>>(
                          File.ReadAllText(_indexPath),
                          JsonOptions)
                      ?? []
                    : [];
                var changed = false;

                foreach (var legacyRecord in legacyRecords)
                {
                    var legacyOutcomePath = Path.Combine(
                        _legacyReportsDirectory,
                        $"{legacyRecord.DiagnosisId:N}.json");
                    if (!File.Exists(legacyOutcomePath))
                    {
                        continue;
                    }

                    var newOutcomePath = Path.Combine(
                        _reportsDirectory,
                        $"{legacyRecord.DiagnosisId:N}.json");
                    if (!File.Exists(newOutcomePath))
                    {
                        File.Copy(
                            legacyOutcomePath,
                            newOutcomePath,
                            overwrite: false);
                    }

                    var existingIndex = currentRecords.FindIndex(
                        record =>
                            record.DiagnosisId == legacyRecord.DiagnosisId);
                    if (existingIndex < 0)
                    {
                        currentRecords.Add(new DiagnosisRecord
                        {
                            DiagnosisId = legacyRecord.DiagnosisId,
                            CreatedAt = legacyRecord.CreatedAt.ToUniversalTime(),
                            LogFileName = legacyRecord.LogFileName,
                            Summary = legacyRecord.Summary,
                            RiskLevel = legacyRecord.RiskLevel,
                            Confidence = legacyRecord.Confidence,
                            SafetyStatus = legacyRecord.SafetyStatus,
                            DiagnosisOutcomePath = newOutcomePath
                        });
                        changed = true;
                    }
                    else if (!string.Equals(
                                 Path.GetFullPath(
                                     currentRecords[existingIndex]
                                         .DiagnosisOutcomePath),
                                 Path.GetFullPath(newOutcomePath),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        var existing = currentRecords[existingIndex];
                        currentRecords[existingIndex] = new DiagnosisRecord
                        {
                            DiagnosisId = existing.DiagnosisId,
                            CreatedAt = existing.CreatedAt.ToUniversalTime(),
                            LogFileName = existing.LogFileName,
                            Summary = existing.Summary,
                            RiskLevel = existing.RiskLevel,
                            Confidence = existing.Confidence,
                            SafetyStatus = existing.SafetyStatus,
                            DiagnosisOutcomePath = newOutcomePath
                        };
                        changed = true;
                    }
                }

                if (!changed)
                {
                    TryMarkLegacyMigrationCompleted();
                    return;
                }

                currentRecords.Sort(
                    (left, right) =>
                        right.CreatedAt.CompareTo(left.CreatedAt));
                var temporaryPath = _indexPath + ".migration.tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(currentRecords, JsonOptions));
                File.Move(temporaryPath, _indexPath, overwrite: true);
                TryMarkLegacyMigrationCompleted();
            }
            catch
            {
                // Compatibility import is best-effort and never removes old
                // files. A failure must not block startup or existing history.
            }
        }

        private void TryMarkLegacyMigrationCompleted()
        {
            try
            {
                Directory.CreateDirectory(_reportsDirectory);
                File.WriteAllText(
                    _legacyMigrationMarkerPath,
                    DateTimeOffset.UtcNow.ToString("O"));
            }
            catch
            {
                // Failure to write the marker must not block history access.
                // Once records.json exists, the next startup still skips
                // legacy import and retries writing this marker.
            }
        }
    }
}
