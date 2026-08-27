using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>一次分析运行结果：成功时含 Result，失败时含原因与请求次数。</summary>
    public sealed record SessionAnalysisRun(
        bool Success,
        SessionAnalysisResult? Result,
        string? ErrorMessage,
        IReadOnlyList<string> ValidationErrors,
        int RequestCount,
        TimeSpan Duration,
        string ModelName,
        bool RepairUsed);

    /// <summary>
    /// 独立的 Session AI 分析服务（§4）：只接受压缩后的分析上下文，
    /// 从 API 层防止把完整 session.json 或 raw samples 送进模型（§19）。
    /// </summary>
    public interface ISessionAnalysisService
    {
        Task<SessionAnalysisRun> AnalyzeAsync(
            TelemetrySessionAnalyzer.TelemetryAnalysisContext context,
            CancellationToken cancellationToken);
    }
}


