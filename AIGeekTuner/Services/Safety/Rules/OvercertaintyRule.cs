using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety.Rules
{
    public sealed class OvercertaintyRule : ISafetyRule
    {
        private readonly SafetyGuardOptions _options;

        public OvercertaintyRule(SafetyGuardOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public SafetyRuleResult Evaluate(DiagnosticResult result)
        {
            if (result.Confidence >= _options.LowConfidenceThreshold)
            {
                return SafetyRuleResult.Pass;
            }

            var diagnosticText = SafetyTextExtractor.GetAllDiagnosticText(result);
            var matchedPhrase = _options.AbsoluteCertaintyPhrases.FirstOrDefault(phrase =>
                !string.IsNullOrWhiteSpace(phrase) &&
                diagnosticText.Contains(phrase, StringComparison.OrdinalIgnoreCase));

            return matchedPhrase is null
                ? SafetyRuleResult.Pass
                : SafetyRuleResult.Warning(
                    $"AI 置信度仅为 {result.Confidence:0.##}，但使用了过度确定性表达“{matchedPhrase}”。");
        }
    }
}
