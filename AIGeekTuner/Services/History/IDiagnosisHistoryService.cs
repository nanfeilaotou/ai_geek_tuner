using AIGeekTuner.Models;

namespace AIGeekTuner.Services.History
{
    public interface IDiagnosisHistoryService
    {
        /// <summary>保存一次成功诊断（含本次使用的模型、Provider 与耗时）。</summary>
        Task<DiagnosisRecord> SaveSuccessAsync(
            DiagnosisOutcome outcome,
            string modelName,
            long durationMs,
            CancellationToken cancellationToken,
            string? providerName = null);

        /// <summary>保存一次失败诊断（仅保留用户可读的原因与元数据，不伪造结果）。</summary>
        Task<DiagnosisRecord> SaveFailureAsync(
            DiagnosisFailureInfo failure,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<DiagnosisRecord>> GetRecordsAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// 加载详情。成功记录返回 Outcome；失败记录 Succeeded=false 且 Outcome 为空。
        /// 同时兼容旧版“直接序列化 DiagnosisOutcome”的详情文件。
        /// </summary>
        Task<DiagnosisHistoryDetail> LoadDetailAsync(
            DiagnosisRecord record,
            CancellationToken cancellationToken);

        Task DeleteAsync(
            Guid diagnosisId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 清空全部诊断历史（索引、详情、损坏备份）；
        /// 不触碰设置、日志与旧版迁移标记。
        /// </summary>
        Task ClearAllAsync(CancellationToken cancellationToken = default);
    }
}
