namespace AIGeekTuner.Models
{
    /// <summary>Diagnosis prompt 与 grounding validator 共用的本次输入快照。</summary>
    public sealed record DiagnosticPromptContext(
        string UserMessage,
        IReadOnlyList<DiagnosticEvidenceSource> Sources)
    {
        /// <summary>旧 BuildUserContext API 的 JSON-only 兼容消息。</summary>
        public string LegacyUserMessage { get; init; } = UserMessage;

        public DiagnosticEvidenceSource? FindSource(string? sourceId) =>
            string.IsNullOrWhiteSpace(sourceId)
                ? null
                : Sources.FirstOrDefault(source =>
                    string.Equals(source.Id, sourceId, StringComparison.Ordinal));
    }

    /// <summary>可被 Fact 引用的本次请求原始来源。Knowledge 不进入该集合。</summary>
    public sealed record DiagnosticEvidenceSource(
        string Id,
        string Kind,
        string Label,
        string Content);

    public static class DiagnosticEvidenceSourceIds
    {
        public const string UserDescription = "source:user-description";
        public const string FaultLog = "source:fault-log";
        public const string HardwareContext = "source:hardware-context";
        public const string SystemContext = "source:system-context";
    }
}
