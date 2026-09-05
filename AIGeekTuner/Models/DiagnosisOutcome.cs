namespace AIGeekTuner.Models
{
    public sealed class DiagnosisOutcome
    {
        public Guid DiagnosisId { get; init; } = Guid.NewGuid();

        public required DiagnosticRequest Request { get; init; }

        public required DiagnosticResult AiResult { get; init; }

        public required SafetyResult Safety { get; init; }

        public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

        // ---- V2-M5.1B：runtime 元数据（Gate L，additive；旧文件缺省为 null） ----

        /// <summary>实际产生本次结果的模型 ID（来自运行时快照）。</summary>
        public string? ModelName { get; init; }

        /// <summary>实际使用的 Provider 稳定 ID。</summary>
        public string? ProviderId { get; init; }

        /// <summary>实际使用的 Provider 显示名（用于事后溯源，非密钥信息）。</summary>
        public string? ProviderDisplayName { get; init; }

        public bool CanDisplayAiResult => Safety.IsApproved;
    }
}
