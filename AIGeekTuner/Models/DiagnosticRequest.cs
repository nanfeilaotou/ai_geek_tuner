namespace AIGeekTuner.Models
{
    public sealed class DiagnosticRequest
    {
        public required HardwareInfo Hardware { get; init; }

        public required FaultLog FaultLog { get; init; }

        public SystemContext SystemContext { get; init; } = new();

        public IReadOnlyList<DiagnosticKnowledgeEntry> KnowledgeContext { get; init; } =
            Array.Empty<DiagnosticKnowledgeEntry>();

        public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
