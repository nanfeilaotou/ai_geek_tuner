using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Diagnosis
{
    /// <summary>
    /// 诊断证据的确定性来源校验。它只负责“Fact 是否能在本次 source
    /// snapshot 中逐字符找到”，不参与危险操作或过度确定性安全审核。
    /// </summary>
    public sealed class DiagnosticGroundingValidator
    {
        public bool Validate(
            DiagnosticResult result,
            DiagnosticPromptContext context)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(context);

            if (result.Evidence is null)
            {
                throw new DiagnosticGroundingValidationException(["Evidence 为空"]);
            }

            if (context.Sources is null)
            {
                throw new DiagnosticGroundingValidationException(["Sources 为空"]);
            }

            var errors = new List<string>();
            for (var index = 0; index < result.Evidence.Count; index++)
            {
                var evidence = result.Evidence[index];
                if (evidence is null)
                {
                    errors.Add($"Fact[{index}] Evidence 为空");
                    continue;
                }

                if (evidence.Kind != EvidenceKind.Fact)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(evidence.SourceId))
                {
                    errors.Add($"Fact[{index}] 缺少 SourceId");
                    continue;
                }

                var source = context.FindSource(evidence.SourceId);
                if (source is null)
                {
                    errors.Add($"Fact[{index}] Invalid SourceId '{evidence.SourceId}'");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(evidence.SourceQuote))
                {
                    errors.Add($"Fact[{index}] 缺少 SourceQuote");
                    continue;
                }

                var quote = evidence.SourceQuote.Trim();
                if (!source.Content.Contains(quote, StringComparison.Ordinal))
                {
                    errors.Add($"Fact[{index}] Quote not found in {source.Id}");
                }
            }

            if (errors.Count > 0)
            {
                throw new DiagnosticGroundingValidationException(errors);
            }

            return true;
        }
    }
}
