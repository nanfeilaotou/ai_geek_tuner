namespace AIGeekTuner.Models
{
    public sealed class DiagnosticRequest
    {
        /// <summary>一次诊断输入/操作的稳定身份；历史旧 JSON 缺省时自动为 Empty。</summary>
        public Guid RequestId { get; init; } = Guid.NewGuid();

        public required HardwareInfo Hardware { get; init; }

        public required FaultLog FaultLog { get; init; }

        /// <summary>用户补充的问题描述，与原始故障日志保持独立。</summary>
        public string? UserDescription { get; init; }

        public SystemContext SystemContext { get; init; } = new();

        public IReadOnlyList<DiagnosticKnowledgeEntry> KnowledgeContext { get; init; } =
            Array.Empty<DiagnosticKnowledgeEntry>();

        public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
