using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety.Rules
{
    public sealed class DangerousOperationRule : ISafetyRule
    {
        private readonly IReadOnlyList<string> _dangerousPhrases;

        public DangerousOperationRule(SafetyGuardOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _dangerousPhrases = options.DangerousOperationPhrases;
        }

        public SafetyRuleResult Evaluate(DiagnosticResult result)
        {
            var recommendationText = SafetyTextExtractor.GetRecommendationText(result);
            var matchedPhrase = _dangerousPhrases.FirstOrDefault(phrase =>
                !string.IsNullOrWhiteSpace(phrase) &&
                recommendationText.Contains(phrase, StringComparison.OrdinalIgnoreCase));

            return matchedPhrase is null
                ? SafetyRuleResult.Pass
                : SafetyRuleResult.Reject(
                    $"检测到试图禁用或绕过系统保护的危险建议：{matchedPhrase}。");
        }
    }
}
