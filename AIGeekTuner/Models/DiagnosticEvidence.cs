namespace AIGeekTuner.Models
{
    public sealed class DiagnosticEvidence
    {
        public required EvidenceKind Kind { get; init; }

        public required string Description { get; init; }

        /// <summary>Fact 所引用的本次 DiagnosticPromptContext source。</summary>
        public string? SourceId { get; init; }

        /// <summary>必须是 SourceId.Content 的逐字符子串；旧历史缺省时为 null。</summary>
        public string? SourceQuote { get; init; }
    }
}
