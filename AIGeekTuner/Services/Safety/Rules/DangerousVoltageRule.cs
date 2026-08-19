using System.Globalization;
using System.Text.RegularExpressions;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Safety.Rules
{
    public sealed class DangerousVoltageRule : ISafetyRule
    {
        private readonly SafetyGuardOptions _options;
        private readonly Regex _voltageRegex;

        public DangerousVoltageRule(SafetyGuardOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            var parameters = options.MonitoredVoltageParameters
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderByDescending(value => value.Length)
                .Select(Regex.Escape);
            var parameterPattern = string.Join("|", parameters);
            if (string.IsNullOrWhiteSpace(parameterPattern))
            {
                throw new ArgumentException("必须配置至少一个电压参数。", nameof(options));
            }

            _voltageRegex = new Regex(
                $"(?<parameter>{parameterPattern})(?![A-Za-z0-9_])[^0-9\\r\\n]{{0,32}}(?<value>\\d+(?:[.,]\\d+)?)\\s*(?:V|伏特)(?![A-Za-z])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }

        public SafetyRuleResult Evaluate(DiagnosticResult result)
        {
            var recommendationText = SafetyTextExtractor.GetRecommendationText(result);
            foreach (Match match in _voltageRegex.Matches(recommendationText))
            {
                var normalizedValue = match.Groups["value"].Value.Replace(',', '.');
                if (!decimal.TryParse(
                        normalizedValue,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var volts))
                {
                    continue;
                }

                if (volts > _options.RejectVoltageAboveVolts ||
                    volts <= _options.RejectVoltageAtOrBelowVolts)
                {
                    var parameter = match.Groups["parameter"].Value;
                    return SafetyRuleResult.Reject(
                        $"检测到明显异常的硬件电压建议：{parameter} {volts.ToString(CultureInfo.InvariantCulture)}V。");
                }
            }

            return SafetyRuleResult.Pass;
        }
    }
}
