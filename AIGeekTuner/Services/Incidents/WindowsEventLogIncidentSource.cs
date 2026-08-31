using System.Diagnostics.Eventing.Reader;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Incidents
{
    /// <summary>
    /// IIncidentSource 第一实现（Gate C–F）：
    /// 只查 System / Application 两个 channel，有界窗口 + 有界结果数。
    /// 失败隔离：单 channel 失败 → Partial；异常细节进 ExceptionLogWriter，
    /// 用户只见简短状态。取消立即停止枚举。
    /// </summary>
    public sealed class WindowsEventLogIncidentSource : IIncidentSource
    {
        public static readonly IReadOnlyList<string> SupportedChannels = ["System", "Application"];

        private readonly IWindowsEventRecordReader _reader;

        public WindowsEventLogIncidentSource(IWindowsEventRecordReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public async Task<IncidentQueryResult> QueryAsync(
            IncidentQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            query.Validate();

            var channelResults = new List<IncidentChannelResult>();
            var rawEvents = new List<RawWindowsEvent>();

            foreach (var channel in SupportedChannels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var raws = await _reader.ReadAsync(
                        channel, query.StartUtc, query.EndUtc, query.MaxResults,
                        cancellationToken).ConfigureAwait(false);
                    rawEvents.AddRange(raws);
                    channelResults.Add(new IncidentChannelResult(
                        channel, IncidentQueryStatus.Success, null, raws.Count));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UnauthorizedAccessException exception)
                {
                    ExceptionLogWriter.Write(exception, $"IncidentQuery/{channel}");
                    channelResults.Add(new IncidentChannelResult(
                        channel, IncidentQueryStatus.PermissionDenied,
                        "没有读取该事件日志的权限。", 0));
                }
                catch (EventLogNotFoundException exception)
                {
                    ExceptionLogWriter.Write(exception, $"IncidentQuery/{channel}");
                    channelResults.Add(new IncidentChannelResult(
                        channel, IncidentQueryStatus.Unavailable,
                        "该事件日志在本机不可用。", 0));
                }
                catch (Exception exception)
                {
                    ExceptionLogWriter.Write(exception, $"IncidentQuery/{channel}");
                    channelResults.Add(new IncidentChannelResult(
                        channel, IncidentQueryStatus.Error,
                        "事件日志查询失败。", 0));
                }
            }

            var incidents = BuildIncidents(rawEvents, query.MaxResults);
            return new IncidentQueryResult(incidents, channelResults)
            {
                Status = AggregateStatus(channelResults),
            };
        }

        /// <summary>映射 → 去重 → 时间升序 → 保留最近 MaxResults 条 → 稳定 EvidenceId。</summary>
        internal static IReadOnlyList<WindowsIncident> BuildIncidents(
            IReadOnlyList<RawWindowsEvent> rawEvents, int maxResults)
        {
            var mapped = rawEvents
                .Select(WindowsIncidentMapper.Map)
                .Where(incident => incident is not null)
                .Select(incident => incident!)
                .Distinct(DedupKeyComparer.Instance)
                .OrderByDescending(incident => incident.OccurredAtUtc)
                .ThenByDescending(incident => incident.RecordId ?? long.MinValue)
                .Take(maxResults)
                .OrderBy(incident => incident.OccurredAtUtc)
                .ThenBy(incident => incident.RecordId ?? long.MinValue)
                .ToArray();

            var results = new List<WindowsIncident>(mapped.Length);
            for (var i = 0; i < mapped.Length; i++)
            {
                results.Add(mapped[i] with
                {
                    EvidenceId = $"incident:{(i + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}",
                });
            }

            return results;
        }

        internal static IncidentQueryStatus AggregateStatus(
            IReadOnlyList<IncidentChannelResult> channels)
        {
            var distinct = channels.Select(c => c.Status).Distinct().ToArray();
            if (distinct.Length == 1)
            {
                return distinct[0];
            }

            return distinct.Contains(IncidentQueryStatus.Success)
                ? IncidentQueryStatus.Partial
                : IncidentQueryStatus.Error;
        }

        /// <summary>Gate E：Channel + RecordId 优先；RecordId 缺失时保守 fallback。</summary>
        private sealed class DedupKeyComparer : IEqualityComparer<WindowsIncident>
        {
            public static readonly DedupKeyComparer Instance = new();

            public bool Equals(WindowsIncident? x, WindowsIncident? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x is null || y is null)
                {
                    return false;
                }

                if (x.RecordId.HasValue && y.RecordId.HasValue)
                {
                    return string.Equals(x.Channel, y.Channel, StringComparison.OrdinalIgnoreCase)
                        && x.RecordId == y.RecordId;
                }

                return string.Equals(x.ProviderName, y.ProviderName, StringComparison.OrdinalIgnoreCase)
                    && x.EventId == y.EventId
                    && x.OccurredAtUtc == y.OccurredAtUtc
                    && string.Equals(x.Summary, y.Summary, StringComparison.Ordinal);
            }

            public int GetHashCode(WindowsIncident obj) =>
                obj.RecordId.HasValue
                    ? HashCode.Combine(obj.Channel.ToLowerInvariant(), obj.RecordId.Value)
                    : HashCode.Combine(
                        obj.ProviderName.ToLowerInvariant(),
                        obj.EventId,
                        obj.OccurredAtUtc,
                        obj.Summary);
        }
    }

    /// <summary>M4.1 事件获取入口（命名与项目现有 XxxService/Source 风格一致）。</summary>
    public interface IIncidentSource
    {
        Task<IncidentQueryResult> QueryAsync(
            IncidentQuery query, CancellationToken cancellationToken = default);
    }
}
