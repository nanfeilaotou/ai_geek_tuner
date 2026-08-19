namespace AIGeekTuner.Models
{
    public sealed class DiagnosticResult
    {
        public required string Summary { get; init; }

        public required string RootCause { get; init; }

        public required double Confidence { get; init; }

        public required DiagnosticRiskLevel RiskLevel { get; init; }

        public required IReadOnlyList<DiagnosticEvidence> Evidence { get; init; }

        public required IReadOnlyList<Recommendation> Recommendations { get; init; }
    }
}
