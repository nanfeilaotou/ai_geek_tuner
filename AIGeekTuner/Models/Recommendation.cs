namespace AIGeekTuner.Models
{
    public sealed class Recommendation
    {
        public required string Action { get; init; }

        public required string Reason { get; init; }

        public required DiagnosticRiskLevel RiskLevel { get; init; }

        public IReadOnlyList<string> Precautions { get; init; } = Array.Empty<string>();
    }
}
