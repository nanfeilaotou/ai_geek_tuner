using System;

namespace AIGeekTuner.Models.Incidents
{
    /// <summary>
    /// Session ↔ Windows Incident 关联信封（V2-M4.2）。
    ///
    /// 证据分层不变量（Gate B）：
    /// - session.json   = telemetry deterministic evidence（永不掺入 AI 结果或 Windows 事件）；
    /// - incidents.json = Windows deterministic evidence（本文件）；
    /// - analysis.json  = AI inference。
    ///
    /// 本信封只记录“这些 Windows incident 落在该 Session 的关联查询窗口内”这一
    /// 确定性事实——correlation ≠ causation：不做任何因果推断，不携带
    /// RootCause / CorrelationScore 之类的推断字段。因果解释属 M4.3 AI 层。
    /// </summary>
    public sealed record SessionIncidentEnvelope(
        int SchemaVersion,
        string SessionId,
        DateTimeOffset QueriedAtUtc,
        DateTimeOffset WindowStartUtc,
        DateTimeOffset WindowEndUtc,
        TimeSpan PreBuffer,
        TimeSpan PostBuffer,
        IncidentQueryStatus QueryStatus,
        IReadOnlyList<IncidentChannelResult> Channels,
        IReadOnlyList<WindowsIncident> Incidents);
}
