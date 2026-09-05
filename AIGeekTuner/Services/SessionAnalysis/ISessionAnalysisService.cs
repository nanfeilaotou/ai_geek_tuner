using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>一次分析运行结果：成功时含 Result，失败时含原因与请求次数。</summary>
    /// <remarks>
    /// EvidenceContextJson（V2-M4.3）：实际发送给模型的 DiagnosticEvidenceContext 序列化文本，
    /// 用于 analysis.json ContextJson 存档——“AI 当时到底看到了哪些 telemetry + incidents”。
    /// 传输失败时也填充（表示本应发送的内容）。
    /// </remarks>
    public sealed record SessionAnalysisRun(
        bool Success,
        SessionAnalysisResult? Result,
        string? ErrorMessage,
        IReadOnlyList<string> ValidationErrors,
        int RequestCount,
        TimeSpan Duration,
        string ModelName,
        bool RepairUsed,
        string? EvidenceContextJson = null,
        // ---- V2-M5.1B（Gate L）：实际使用的 Provider 元数据（additive；旧调用缺省 null） ----
        string? ProviderId = null,
        string? ProviderName = null);

    /// <summary>
    /// 独立的 Session AI 分析服务（§4）：只接受组合后的有界确定性证据上下文，
    /// 从 API 层防止把完整 session.json、raw samples 或完整 incidents.json 送进模型（§19）。
    /// </summary>
    public interface ISessionAnalysisService
    {
        Task<SessionAnalysisRun> AnalyzeAsync(
            DiagnosticEvidenceContext context,
            CancellationToken cancellationToken);
    }
}
