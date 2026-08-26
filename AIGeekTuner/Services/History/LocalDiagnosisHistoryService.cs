using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.History
{
    public sealed class LocalDiagnosisHistoryService : IDiagnosisHistoryService
    {
        private const string IndexFileName = "records.json";
        private const string LegacyMigrationMarkerFileName =
            ".legacy-history-imported";
        private const string PastedLogName = "粘贴日志";
        private const string CorruptBackupPrefix = "records.corrupt-";

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

        public async Task<DiagnosisRecord> SaveSuccessAsync(
            DiagnosisOutcome outcome,
            string modelName,
            long durationMs,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(outcome);

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_reportsDirectory);

                var outcomePath = DetailPathFor(outcome.DiagnosisId);
                var record = CreateSuccessRecord(outcome, modelName, durationMs, outcomePath);

                var detail = new DiagnosisHistoryDetail
                {
                    SchemaVersion = 2,
                    Succeeded = true,
                    Outcome = outcome,
                    ModelName = modelName,
                    DurationMs = durationMs
                };
                await WriteJsonAtomicallyAsync(outcomePath, detail, cancellationToken);

                await UpsertIndexAsync(record, cancellationToken);
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

        public async Task<DiagnosisRecord> SaveFailureAsync(
            DiagnosisFailureInfo failure,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(failure);

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_reportsDirectory);

                var diagnosisId = Guid.NewGuid();
                var detailPath = DetailPathFor(diagnosisId);
                var reason = TruncateReason(failure.FailureReason);

                var detail = new DiagnosisHistoryDetail
                {
                    SchemaVersion = 2,
                    Succeeded = false,
                    FailureReason = reason,
                    FailureCode = failure.FailureCode,
                    ModelName = failure.ModelName,
                    DurationMs = failure.DurationMs,
                    CompletedAt = failure.CompletedAt.ToUniversalTime()
                };
                await WriteJsonAtomicallyAsync(detailPath, detail, cancellationToken);

                var record = new DiagnosisRecord
                {
                    DiagnosisId = diagnosisId,
                    CreatedAt = failure.CompletedAt.ToUniversalTime(),
                    LogFileName = string.IsNullOrWhiteSpace(failure.LogFileName)
                        ? PastedLogName
                        : failure.LogFileName!,
                    Summary = reason,
                    RiskLevel = DiagnosticRiskLevel.Low,
                    Confidence = 0,
                    SafetyStatus = SafetyStatus.Pending,
                    DiagnosisOutcomePath = detailPath,
                    Succeeded = false,
                    ModelName = failure.ModelName,
                    DurationMs = failure.DurationMs,
                    FailureReason = reason
                };

                await UpsertIndexAsync(record, cancellationToken);
                return record;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new DiagnosisHistoryException(
                    "诊断失败原因无法写入本地历史记录。请检查应用数据目录的访问权限。",
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
                List<DiagnosisRecord> records;
                try
                {
                    records = await ReadRecordsCoreAsync(cancellationToken);
                }
                catch (JsonException exception)
                {
                    // 索引损坏不允许让 History 永久瘫痪：
                    // 留痕 → 备份坏索引 → 从详情文件尽可能重建。
                    ExceptionLogWriter.Write(exception, "History index corrupt");
                    BackupCorruptIndex();
                    records = RebuildIndexFromDetails(cancellationToken);
                    Directory.CreateDirectory(_reportsDirectory);
                    await WriteJsonAtomicallyAsync(_indexPath, records, cancellationToken);
                }

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
                    "无法读取本地诊断历史记录。请检查应用数据目录的访问权限。",
                    exception);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        public async Task<DiagnosisHistoryDetail> LoadDetailAsync(
            DiagnosisRecord record,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);

            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                var detailPath = ValidateDetailPath(record);
                if (!File.Exists(detailPath))
                {
                    throw new FileNotFoundException(
                        "诊断报告文件不存在。",
                        detailPath);
                }

                var json = await File.ReadAllTextAsync(detailPath, cancellationToken);
                var detail = ParseDetail(json);
                if (detail.Succeeded && detail.Outcome is not null &&
                    detail.Outcome.DiagnosisId != record.DiagnosisId)
                {
                    throw new JsonException("诊断报告内容无效或与历史记录不匹配。");
                }

                return detail;
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

                var detailPath = DetailPathFor(diagnosisId);
                if (File.Exists(detailPath))
                {
                    File.Delete(detailPath);
                }

                if (removedCount > 0)
                {
                    Directory.CreateDirectory(_reportsDirectory);
                    await WriteJsonAtomicallyAsync(_indexPath, records, cancellationToken);
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

        public async Task ClearAllAsync(CancellationToken cancellationToken = default)
        {
            await _storageLock.WaitAsync(cancellationToken);
            try
            {
                if (!Directory.Exists(_reportsDirectory))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(_reportsDirectory))
                {
                    var name = Path.GetFileName(file);

                    // 保留旧版迁移标记，否则下次启动会把旧数据再次导入。
                    if (string.Equals(name, LegacyMigrationMarkerFileName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    File.Delete(file);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new DiagnosisHistoryException(
                    "无法清空本地诊断历史，请检查应用数据目录的访问权限。",
                    exception);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        private async Task UpsertIndexAsync(
            DiagnosisRecord record,
            CancellationToken cancellationToken)
        {
            var records = await ReadRecordsCoreAsync(cancellationToken);
            records.RemoveAll(item => item.DiagnosisId == record.DiagnosisId);
            records.Add(record);
            records.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));

            Directory.CreateDirectory(_reportsDirectory);
            await WriteJsonAtomicallyAsync(_indexPath, records, cancellationToken);
        }

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

        /// <summary>
        /// 从详情文件重建索引：只信任详情本身；
        /// 单个损坏/无关文件跳过并留痕，绝不让一条坏文件阻塞整个历史。
        /// </summary>
        private List<DiagnosisRecord> RebuildIndexFromDetails(
            CancellationToken cancellationToken)
        {
            var rebuilt = new List<DiagnosisRecord>();
            if (!Directory.Exists(_reportsDirectory))
            {
                return rebuilt;
            }

            foreach (var file in Directory.GetFiles(_reportsDirectory, "*.json"))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(CorruptBackupPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!Guid.TryParseExact(
                        Path.GetFileNameWithoutExtension(file),
                        "N",
                        out var diagnosisId))
                {
                    continue;
                }

                try
                {
                    var detail = ParseDetail(File.ReadAllText(file));
                    rebuilt.Add(BuildRecordFromDetail(diagnosisId, file, detail));
                }
                catch (Exception exception) when (
                    exception is JsonException or ArgumentException)
                {
                    ExceptionLogWriter.Write(
                        exception,
                        $"History rebuild skipped {fileName}");
                }
            }

            return rebuilt;
        }

        private static DiagnosisRecord BuildRecordFromDetail(
            Guid diagnosisId,
            string path,
            DiagnosisHistoryDetail detail)
        {
            var completedAt = detail.CompletedAt != default
                ? detail.CompletedAt
                : File.GetLastWriteTimeUtc(path);

            if (detail.Succeeded && detail.Outcome is not null)
            {
                var outcome = detail.Outcome;
                return new DiagnosisRecord
                {
                    DiagnosisId = diagnosisId,
                    CreatedAt = outcome.CompletedAt.ToUniversalTime(),
                    LogFileName = string.IsNullOrWhiteSpace(outcome.Request.FaultLog.FileName)
                        ? PastedLogName
                        : outcome.Request.FaultLog.FileName!,
                    Summary = outcome.AiResult.Summary,
                    RiskLevel = outcome.AiResult.RiskLevel,
                    Confidence = Math.Clamp(outcome.AiResult.Confidence, 0, 1),
                    SafetyStatus = outcome.Safety.Status,
                    DiagnosisOutcomePath = path,
                    Succeeded = true,
                    ModelName = detail.ModelName,
                    DurationMs = detail.DurationMs
                };
            }

            return new DiagnosisRecord
            {
                DiagnosisId = diagnosisId,
                CreatedAt = completedAt.ToUniversalTime(),
                LogFileName = PastedLogName,
                Summary = detail.FailureReason ?? "(失败原因缺失)",
                RiskLevel = DiagnosticRiskLevel.Low,
                Confidence = 0,
                SafetyStatus = SafetyStatus.Pending,
                DiagnosisOutcomePath = path,
                Succeeded = false,
                ModelName = detail.ModelName,
                DurationMs = detail.DurationMs,
                FailureReason = detail.FailureReason
            };
        }

        /// <summary>
        /// 解析详情文本：优先 v2 封装；SchemaVersion 缺省时尝试旧版
        /// “直接序列化 DiagnosisOutcome”并包装为成功封装。
        /// </summary>
        private static DiagnosisHistoryDetail ParseDetail(string json)
        {
            var envelope = JsonSerializer.Deserialize<DiagnosisHistoryDetail>(
                json, JsonOptions);
            if (envelope is not null && envelope.SchemaVersion == 2)
            {
                return envelope;
            }

            var legacy = JsonSerializer.Deserialize<DiagnosisOutcome>(json, JsonOptions);
            if (legacy is not null)
            {
                return new DiagnosisHistoryDetail
                {
                    Succeeded = true,
                    Outcome = legacy,
                    ModelName = null,
                    DurationMs = null
                };
            }

            throw new JsonException("诊断报告内容无效。");
        }

        private static DiagnosisRecord CreateSuccessRecord(
            DiagnosisOutcome outcome,
            string modelName,
            long durationMs,
            string outcomePath) => new()
        {
            DiagnosisId = outcome.DiagnosisId,
            CreatedAt = outcome.CompletedAt.ToUniversalTime(),
            LogFileName = string.IsNullOrWhiteSpace(outcome.Request.FaultLog.FileName)
                ? PastedLogName
                : outcome.Request.FaultLog.FileName!,
            Summary = outcome.AiResult.Summary,
            RiskLevel = outcome.AiResult.RiskLevel,
            Confidence = Math.Clamp(outcome.AiResult.Confidence, 0, 1),
            SafetyStatus = outcome.Safety.Status,
            DiagnosisOutcomePath = outcomePath,
            Succeeded = true,
            ModelName = modelName,
            DurationMs = durationMs
        };

        private string DetailPathFor(Guid diagnosisId) =>
            Path.Combine(_reportsDirectory, $"{diagnosisId:N}.json");

        private string ValidateDetailPath(DiagnosisRecord record)
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

        private static string TruncateReason(string reason)
        {
            var normalized = reason.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= 300
                ? normalized
                : normalized[..300] + "…";
        }

        private void BackupCorruptIndex()
        {
            try
            {
                if (!File.Exists(_indexPath))
                {
                    return;
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(
                    _indexPath,
                    Path.Combine(_reportsDirectory, $"{CorruptBackupPrefix}{stamp}.json"),
                    overwrite: false);
            }
            catch (Exception backupFailure)
            {
                ExceptionLogWriter.Write(backupFailure, "History index backup");
            }
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
                or SecurityException
                or JsonException
                or NotSupportedException
                or ArgumentException;

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
                            DiagnosisOutcomePath = newOutcomePath,
                            Succeeded = legacyRecord.Succeeded,
                            ModelName = legacyRecord.ModelName,
                            DurationMs = legacyRecord.DurationMs,
                            FailureReason = legacyRecord.FailureReason
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
                            DiagnosisOutcomePath = newOutcomePath,
                            Succeeded = existing.Succeeded,
                            ModelName = existing.ModelName,
                            DurationMs = existing.DurationMs,
                            FailureReason = existing.FailureReason
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
