using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Knowledge
{
    public sealed class DiagnosticKnowledgeService : IDiagnosticKnowledgeService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _knowledgeDirectory;
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private IReadOnlyList<DiagnosticKnowledgeEntry>? _entries;

        public DiagnosticKnowledgeService(string? knowledgeDirectory = null)
        {
            _knowledgeDirectory = Path.GetFullPath(
                knowledgeDirectory ?? Path.Combine(
                    AppContext.BaseDirectory,
                    "KnowledgeBase"));
        }

        public async Task<IReadOnlyList<DiagnosticKnowledgeEntry>> FindMatchesAsync(
            FaultLog faultLog,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(faultLog);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(faultLog.Content))
            {
                return [];
            }

            var entries = await GetEntriesAsync(cancellationToken);
            return entries
                .Select(entry => new
                {
                    Entry = entry,
                    MatchCount = entry.Keywords.Count(keyword =>
                        faultLog.Content.Contains(
                            keyword,
                            StringComparison.OrdinalIgnoreCase))
                })
                .Where(match => match.MatchCount > 0)
                .OrderByDescending(match => match.MatchCount)
                .ThenBy(match => match.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(match => match.Entry)
                .ToArray();
        }

        private async Task<IReadOnlyList<DiagnosticKnowledgeEntry>> GetEntriesAsync(
            CancellationToken cancellationToken)
        {
            if (_entries is not null)
            {
                return _entries;
            }

            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                if (_entries is not null)
                {
                    return _entries;
                }

                _entries = await LoadEntriesAsync(cancellationToken);
                return _entries;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private async Task<IReadOnlyList<DiagnosticKnowledgeEntry>> LoadEntriesAsync(
            CancellationToken cancellationToken)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(_knowledgeDirectory))
                {
                    return [];
                }

                files = Directory.GetFiles(
                    _knowledgeDirectory,
                    "*.json",
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return [];
            }

            var entries = new List<DiagnosticKnowledgeEntry>();
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = new FileStream(
                        file,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 8 * 1024,
                        useAsync: true);
                    var entry = await JsonSerializer.DeserializeAsync<DiagnosticKnowledgeEntry>(
                        stream,
                        JsonOptions,
                        cancellationToken);

                    if (IsValid(entry))
                    {
                        entries.Add(entry!);
                    }
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // 单个知识文件不可用时跳过；知识库不能阻断诊断。
                }
            }

            return entries
                .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        private static bool IsValid(DiagnosticKnowledgeEntry? entry) =>
            entry is not null &&
            !string.IsNullOrWhiteSpace(entry.Name) &&
            !string.IsNullOrWhiteSpace(entry.Description) &&
            entry.Keywords is { Count: > 0 } &&
            entry.Keywords.All(keyword => !string.IsNullOrWhiteSpace(keyword)) &&
            entry.CommonCauses is not null &&
            entry.VerificationSteps is not null;
    }
}
