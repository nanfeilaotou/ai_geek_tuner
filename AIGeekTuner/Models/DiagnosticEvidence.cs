namespace AIGeekTuner.Models
{
    public sealed class DiagnosticEvidence
    {
        public required EvidenceKind Kind { get; init; }

        public required string Description { get; init; }
    }
}
