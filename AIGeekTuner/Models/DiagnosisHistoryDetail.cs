namespace AIGeekTuner.Models
{
    /// <summary>
    /// 历史详情文件的新版封装。
    /// SchemaVersion：新写入的详情恒为 2；旧版“直接序列化 DiagnosisOutcome”
    /// 的文件没有该字段，反序列化后保持 0，读取端据此走兼容分支。
    /// </summary>
    public sealed class DiagnosisHistoryDetail
    {
        public int SchemaVersion { get; init; }

        public bool Succeeded { get; init; } = true;

        public string? FailureReason { get; init; }

        public string? FailureCode { get; init; }

        public string? ModelName { get; init; }

        public long? DurationMs { get; init; }

        /// <summary>失败详情的完成时间（成功详情取 Outcome.CompletedAt）。</summary>
        public DateTimeOffset CompletedAt { get; init; }

        public DiagnosisOutcome? Outcome { get; init; }
    }

    /// <summary>失败诊断落库所需的最小信息（不含 Prompt、模型原始输出与堆栈）。</summary>
    public sealed record DiagnosisFailureInfo(
        DateTimeOffset CompletedAt,
        string ModelName,
        long DurationMs,
        string FailureCode,
        string FailureReason,
        string? LogFileName);
}
