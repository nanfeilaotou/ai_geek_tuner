namespace AIGeekTuner.Services.Safety.Rules
{
    public sealed record SafetyRuleResult(
        SafetyRuleDecision Decision,
        IReadOnlyList<string> Messages)
    {
        public static SafetyRuleResult Pass { get; } = new(
            SafetyRuleDecision.Pass,
            Array.Empty<string>());

        public static SafetyRuleResult Warning(string message) => new(
            SafetyRuleDecision.Warning,
            [message]);

        public static SafetyRuleResult Reject(string message) => new(
            SafetyRuleDecision.Reject,
            [message]);
    }
}
