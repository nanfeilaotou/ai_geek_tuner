using System;
using System.Threading;
using System.Threading.Tasks;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.Incidents
{
    /// <summary>
    /// Session ↔ Windows Incident 关联采集（V2-M4.2）：
    /// 已完成的 Telemetry Session → 计算带 buffer 的关联查询窗口 →
    /// IIncidentSource.QueryAsync → SessionIncidentEnvelope → 保存 incidents.json。
    ///
    /// 职责边界（Gate D）：
    /// - 只消费 IncidentQueryResult，绝不重新 Map、绝不重新编号 EvidenceId；
    /// - 不做任何因果推断（correlation ≠ causation）；
    /// - Recorder（TelemetryRecordingService）不依赖本服务：Windows Incident
    ///   correlation 是录制结束后的独立证据采集阶段；
    /// - 查询失败状态（Partial/PermissionDenied/Unavailable/Error）本身也是
    ///   deterministic data-quality information，照常入 envelope 持久化。
    /// </summary>
    public interface ISessionIncidentCorrelationService
    {
        /// <summary>
        /// 对一个已完成的 Session 采集 Windows 事件证据并持久化。
        /// 未完成的 Session（CompletedAtUtc == null）被明确拒绝，绝不偷偷用
        /// UtcNow 补齐结束时间——不给 active recording 生成 incidents.json。
        /// </summary>
        Task<SessionIncidentEnvelope> CaptureAsync(
            TelemetryRecordingSession session,
            CancellationToken cancellationToken = default);
    }

    public sealed class SessionIncidentCorrelationService : ISessionIncidentCorrelationService
    {
        public const int SchemaVersion = 1;

        /// <summary>
        /// 关联查询窗口缓冲：Windows 事件写入时间可能略早/略晚于 telemetry
        /// session 边缘，前后各放宽 30 秒。注意：这是“关联查询窗口”，
        /// 不是“因果判断窗口”——buffer 只保证证据查询覆盖完整，不表达任何
        /// 事件与会话之间的因果关系。固定值，不做 Settings、不给用户配置。
        /// </summary>
        public static readonly TimeSpan PreBuffer = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan PostBuffer = TimeSpan.FromSeconds(30);

        private readonly IIncidentSource _source;
        private readonly ISessionIncidentStore _store;

        public SessionIncidentCorrelationService(IIncidentSource source, ISessionIncidentStore store)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public async Task<SessionIncidentEnvelope> CaptureAsync(
            TelemetryRecordingSession session,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (session.CompletedAtUtc is null)
            {
                throw new InvalidOperationException(
                    $"Session {session.Id} 尚未结束（CompletedAtUtc 为空），拒绝关联 Windows 事件证据。");
            }

            var windowStart = session.StartedAtUtc - PreBuffer;
            var windowEnd = session.CompletedAtUtc.Value + PostBuffer;
            // MaxResults 直接用 acquisition 层硬上限（500）：M4.2 职责是
            // deterministic evidence persistence，不提前为 AI 上下文裁剪。
            // 超过 IncidentQuery.MaxWindow 的超长会话会被 Validate 拒绝并向上抛出
            // （见已知限制），由调用方决定如何降级，绝不静默缩短证据窗口。
            var query = new IncidentQuery(windowStart, windowEnd, IncidentQuery.MaxResultsCap);

            var result = await _source.QueryAsync(query, cancellationToken).ConfigureAwait(false);

            var envelope = new SessionIncidentEnvelope(
                SchemaVersion: SchemaVersion,
                SessionId: session.Id,
                QueriedAtUtc: DateTimeOffset.UtcNow,
                WindowStartUtc: windowStart,
                WindowEndUtc: windowEnd,
                PreBuffer: PreBuffer,
                PostBuffer: PostBuffer,
                QueryStatus: result.Status,
                Channels: result.Channels,
                Incidents: result.Incidents);

            _store.Save(envelope);
            return envelope;
        }
    }
}
