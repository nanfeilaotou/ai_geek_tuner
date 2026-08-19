namespace AIGeekTuner.Models
{
    public sealed class DiagnosisOutcome
    {
        public Guid DiagnosisId { get; init; } = Guid.NewGuid();

        public required DiagnosticRequest Request { get; init; }

        public required DiagnosticResult AiResult { get; init; }

        public required SafetyResult Safety { get; init; }

        public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

        public bool CanDisplayAiResult => Safety.IsApproved;
    }
}
