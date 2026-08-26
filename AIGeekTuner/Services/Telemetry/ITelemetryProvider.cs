using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// Snapshot-oriented 遥测 Provider 契约（M1 最小面）。
    /// 刻意不包含 StartStreaming / Subscribe / EventBus / 持久化——Recorder 属于下一阶段设计。
    /// 实现要求：
    /// 1) 不抛出业务性失败：不可用/未配置一律用 <see cref="TelemetryProviderResult.Status"/> 表达；
    /// 2) 尊重 cancellationToken；
    /// 3) 绝不编造缺失的指标。
    /// </summary>
    public interface ITelemetryProvider
    {
        TelemetrySourceKind SourceKind { get; }

        Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default);
    }
}
