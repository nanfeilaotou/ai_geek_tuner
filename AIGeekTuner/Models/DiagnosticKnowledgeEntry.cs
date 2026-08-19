namespace AIGeekTuner.Models
{
    public sealed class DiagnosticKnowledgeEntry
    {
        public required string Name { get; init; }

        public required string Description { get; init; }

        public required IReadOnlyList<string> Keywords { get; init; }

        public required IReadOnlyList<string> CommonCauses { get; init; }

        public required IReadOnlyList<string> VerificationSteps { get; init; }

        public required DiagnosticRiskLevel RiskLevel { get; init; }
    }
}
