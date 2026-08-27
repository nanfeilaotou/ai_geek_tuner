using AIGeekTuner.Services.Telemetry.Recording;
using System.Text.Json;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.SessionAnalysis
{
    public sealed class SessionAnalysisParseException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public SessionAnalysisParseException(IReadOnlyList<string> errors)
            : base("Session analysis validation failed: " + string.Join(" ", errors))
        {
            Errors = errors;
        }
    }

    /// <summary>
    /// 结构化解析与校验（§13 反编造核心）：schema/枚举/置信度范围/证据 ID 全部校验。
    /// 失败抛出 SessionAnalysisParseException，由服务层决定是否 repair。
    /// </summary>
    public sealed class SessionAnalysisJsonParser
    {
        public const int SpokenSummaryMaxLength = 180;

        private static readonly string[] AllowedCategories =
            ["Thermal", "Performance", "Power", "Clock", "Memory", "Storage", "Stability", "DataQuality", "Other"];

        private static readonly string[] AllowedAssessments =
            ["Normal", "Attention", "PotentialIssue", "InsufficientData"];

        public SessionAnalysisResult Parse(string json, IReadOnlyCollection<string> validEvidenceIds)
        {
            var errors = new List<string>();
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new SessionAnalysisParseException(["JSON 解析失败：" + exception.Message]);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new SessionAnalysisParseException(["顶层必须是 JSON 对象。"]);
                }

                string? summary = ReadString(root, "summary", errors);
                string? assessmentText = ReadString(root, "overallAssessment", errors);
                double? confidence = ReadNumber(root, "confidence", errors);
                string? spoken = ReadString(root, "spokenSummary", errors);

                SessionOverallAssessment? assessment = null;
                if (assessmentText is not null)
                {
                    if (AllowedAssessments.Contains(assessmentText, StringComparer.OrdinalIgnoreCase))
                    {
                        assessment = Enum.Parse<SessionOverallAssessment>(assessmentText, ignoreCase: true);
                    }
                    else
                    {
                        errors.Add("overallAssessment 非法值：" + assessmentText);
                    }
                }

                if (confidence is not null and (< 0d or > 1d))
                {
                    errors.Add("confidence 超出 0.0~1.0：" + confidence);
                }

                if (string.IsNullOrWhiteSpace(summary))
                {
                    errors.Add("summary 缺失或为空。");
                }

                var validIds = validEvidenceIds.ToHashSet(StringComparer.Ordinal);
                var findings = new List<SessionFinding>();
                if (root.TryGetProperty("findings", out var findingsElement)
                    && findingsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in findingsElement.EnumerateArray())
                    {
                        ParseFinding(item, validIds, findings, errors);
                    }
                }

                var recommendations = new List<SessionRecommendation>();
                CollectStrings(root, "recommendations", recommendations,
                    text => new SessionRecommendation(text),
                    (item, converted) => { if (converted is not null) recommendations.Add(converted); });

                var uncertainties = new List<string>();
                CollectStrings(root, "uncertainties", uncertainties,
                    text => text,
                    (item, converted) => { if (converted is not null) uncertainties.Add(converted); });

                ValidateSpokenSummary(spoken, errors);

                if (errors.Count > 0 || assessment is null || confidence is null)
                {
                    throw new SessionAnalysisParseException(errors);
                }

                return new SessionAnalysisResult(
                    summary!,
                    assessment.Value,
                    confidence.Value,
                    findings,
                    recommendations.ToArray(),
                    uncertainties,
                    spoken ?? string.Empty);
            }
        }

        private static void CollectStrings<T>(
            JsonElement root,
            string name,
            ICollection<T> target,
            Func<string, T> convert,
            Action<JsonElement, T?> addIfConverted)
        {
            if (!root.TryGetProperty(name, out var arrayElement)
                || arrayElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    target.Add(convert(text));
                }
            }
        }

        private static void ParseFinding(
            JsonElement item,
            IReadOnlySet<string> validIds,
            ICollection<SessionFinding> findings,
            ICollection<string> errors)
        {
            var title = GetString(item, "title");
            var categoryText = GetString(item, "category");
            var assessment = GetString(item, "assessment");
            var explanation = GetString(item, "explanation");

            if (title is null) errors.Add("finding 缺少 title。");
            if (assessment is null) errors.Add("finding 缺少 assessment。");
            if (explanation is null) errors.Add("finding 缺少 explanation。");

            SessionFindingCategory? category = null;
            if (categoryText is null)
            {
                errors.Add("finding 缺少 category。");
            }
            else if (!AllowedCategories.Contains(categoryText, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("finding category 非法值：" + categoryText);
            }
            else
            {
                category = Enum.Parse<SessionFindingCategory>(categoryText, ignoreCase: true);
            }

            var evidenceIds = new List<string>();
            if (item.TryGetProperty("evidenceIds", out var evidenceElement)
                && evidenceElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var idElement in evidenceElement.EnumerateArray())
                {
                    var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    if (!validIds.Contains(id))
                    {
                        errors.Add("未知证据 ID：" + id + "（不在本次分析上下文中）");
                        continue;
                    }

                    evidenceIds.Add(id);
                }
            }
            else
            {
                errors.Add("finding 缺少 evidenceIds 数组。");
            }

            if (title is not null && assessment is not null && explanation is not null && category is not null)
            {
                findings.Add(new SessionFinding(title, category.Value, assessment, explanation, evidenceIds));
            }
        }

        private static void ValidateSpokenSummary(string? spoken, ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(spoken))
            {
                errors.Add("spokenSummary 缺失或为空。");
                return;
            }

            if (spoken.Length > SpokenSummaryMaxLength)
            {
                errors.Add("spokenSummary 过长（" + spoken.Length + " > " + SpokenSummaryMaxLength + "）。");
            }

            var trimmedStart = spoken.TrimStart();
            if (trimmedStart.StartsWith("#") || trimmedStart.StartsWith("-") || trimmedStart.StartsWith("*")
                || spoken.Contains("**", StringComparison.Ordinal)
                || spoken.Contains("\n-", StringComparison.Ordinal))
            {
                errors.Add("spokenSummary 包含 Markdown 痕迹，必须是纯文本。");
            }
        }

        private static string? ReadString(JsonElement root, string name, List<string> errors)
        {
            if (!root.TryGetProperty(name, out var element))
            {
                errors.Add("缺少字段 " + name + "。");
                return null;
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        private static double? ReadNumber(JsonElement root, string name, List<string> errors)
        {
            if (!root.TryGetProperty(name, out var element))
            {
                errors.Add("缺少字段 " + name + "。");
                return null;
            }

            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
                ? value
                : null;
        }

        private static string? GetString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
    }
}


