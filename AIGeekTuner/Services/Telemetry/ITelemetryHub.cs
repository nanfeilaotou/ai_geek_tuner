using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// 遥测聚合入口：并行读取全部 Provider、隔离单源失败、按固定优先级
    /// 对“同设备 + 同规范指标”选择一个来源（绝不平均多个来源的数值）。
    /// </summary>
    public interface ITelemetryHub
    {
        Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default);
    }
}
