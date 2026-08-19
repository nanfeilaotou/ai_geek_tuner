namespace AIGeekTuner.Models
{
    public sealed class SafetyResult
    {
        public required SafetyStatus Status { get; init; }

        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public bool IsApproved =>
            Status is SafetyStatus.Approved or SafetyStatus.ApprovedWithWarnings;

        public static SafetyResult Pending { get; } = new()
        {
            Status = SafetyStatus.Pending
        };
    }
}
