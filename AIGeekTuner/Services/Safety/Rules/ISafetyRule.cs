using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety.Rules
{
    public interface ISafetyRule
    {
        SafetyRuleResult Evaluate(DiagnosticResult result);
    }
}
