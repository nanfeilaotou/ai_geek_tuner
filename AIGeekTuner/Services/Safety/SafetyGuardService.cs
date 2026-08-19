using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Safety.Rules;

namespace AIGeekTuner.Services.Safety
{
    public sealed class SafetyGuardService : ISafetyService
    {
        private readonly IReadOnlyList<ISafetyRule> _rules;

        public SafetyGuardService(SafetyGuardOptions? options = null)
            : this(CreateDefaultRules(options ?? new SafetyGuardOptions()))
        {
        }

        public SafetyGuardService(IEnumerable<ISafetyRule> rules)
        {
            ArgumentNullException.ThrowIfNull(rules);
            _rules = rules.ToArray();

            if (_rules.Count == 0)
            {
                throw new ArgumentException("SafetyGuard 至少需要一条规则。", nameof(rules));
            }
        }

        public Task<SafetyResult> ValidateAsync(
            DiagnosticResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            ValidateDiagnosticCollections(result);
            cancellationToken.ThrowIfCancellationRequested();

            var evaluations = new List<SafetyRuleResult>(_rules.Count);
            foreach (var rule in _rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                evaluations.Add(rule.Evaluate(result));
            }

            var messages = evaluations
                .SelectMany(evaluation => evaluation.Messages)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var status = evaluations.Any(evaluation =>
                evaluation.Decision == SafetyRuleDecision.Reject)
                ? SafetyStatus.Rejected
                : evaluations.Any(evaluation =>
                    evaluation.Decision == SafetyRuleDecision.Warning)
                    ? SafetyStatus.ApprovedWithWarnings
                    : SafetyStatus.Approved;

            return Task.FromResult(new SafetyResult
            {
                Status = status,
                Warnings = messages
            });
        }

        private static IReadOnlyList<ISafetyRule> CreateDefaultRules(
            SafetyGuardOptions options)
        {
            return
            [
                new DangerousVoltageRule(options),
                new DangerousOperationRule(options),
                new OvercertaintyRule(options)
            ];
        }

        private static void ValidateDiagnosticCollections(DiagnosticResult result)
        {
            if (result.Evidence is null || result.Recommendations is null)
            {
                throw new ArgumentException(
                    "DiagnosticResult 的 Evidence 和 Recommendations 不能为空。",
                    nameof(result));
            }

            if (result.Recommendations.Any(recommendation => recommendation is null) ||
                result.Evidence.Any(evidence => evidence is null))
            {
                throw new ArgumentException(
                    "DiagnosticResult 包含空的 Evidence 或 Recommendation。",
                    nameof(result));
            }
        }
    }
}
