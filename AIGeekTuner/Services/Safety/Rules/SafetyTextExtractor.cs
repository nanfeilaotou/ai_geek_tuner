using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety.Rules
{
    internal static class SafetyTextExtractor
    {
        public static string GetRecommendationText(DiagnosticResult result)
        {
            return string.Join(
                Environment.NewLine,
                result.Recommendations.SelectMany(recommendation =>
                    new[] { recommendation.Action, recommendation.Reason }
                        .Concat(recommendation.Precautions ?? Array.Empty<string>())));
        }

        public static string GetAllDiagnosticText(DiagnosticResult result)
        {
            return string.Join(
                Environment.NewLine,
                new[] { result.Summary, result.RootCause }
                    .Concat(result.Evidence.Select(item => item.Description))
                    .Append(GetRecommendationText(result)));
        }
    }
}
