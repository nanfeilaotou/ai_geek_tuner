namespace AIGeekTuner.Models
{
    public sealed class DiagnosisRecord
    {
        public required Guid DiagnosisId { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required string LogFileName { get; init; }

        public required string Summary { get; init; }

        public required DiagnosticRiskLevel RiskLevel { get; init; }

        public required double Confidence { get; init; }

        public required SafetyStatus SafetyStatus { get; init; }

        public required string DiagnosisOutcomePath { get; init; }
    }
}
