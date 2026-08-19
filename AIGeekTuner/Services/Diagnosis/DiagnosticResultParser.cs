using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Diagnosis
{
    public sealed partial class DiagnosticResultParser
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        public DiagnosticResult Parse(string modelResponse)
        {
            if (string.IsNullOrWhiteSpace(modelResponse))
            {
                throw new DiagnosticResultParsingException("模型响应为空。");
            }

            var responseWithoutThinking = ThinkBlockRegex().Replace(
                modelResponse,
                string.Empty);
            var json = ExtractFirstJsonObject(responseWithoutThinking);

            try
            {
                var result = JsonSerializer.Deserialize<DiagnosticResult>(json, JsonOptions)
                    ?? throw new DiagnosticResultParsingException(
                        "模型 JSON 没有产生诊断对象。");

                Validate(result);
                return result;
            }
            catch (JsonException exception)
            {
                throw new DiagnosticResultParsingException(
                    "模型返回的诊断 JSON 格式错误或缺少必需字段。",
                    exception);
            }
        }

        private static string ExtractFirstJsonObject(string response)
        {
            for (var startIndex = 0; startIndex < response.Length; startIndex++)
            {
                if (response[startIndex] != '{' ||
                    !TryFindObjectEnd(response, startIndex, out var endIndex))
                {
                    continue;
                }

                var candidate = response[startIndex..(endIndex + 1)];
                try
                {
                    using var document = JsonDocument.Parse(candidate);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        return candidate;
                    }
                }
                catch (JsonException)
                {
                    // This balanced block was not JSON. Continue looking for the
                    // first valid JSON object later in the model response.
                }

                startIndex = endIndex;
            }

            throw new DiagnosticResultParsingException(
                "模型响应中未找到有效的 JSON 对象。");
        }

        private static bool TryFindObjectEnd(
            string response,
            int startIndex,
            out int endIndex)
        {
            var depth = 0;
            var isInsideString = false;
            var isEscaped = false;

            for (var index = startIndex; index < response.Length; index++)
            {
                var character = response[index];

                if (isInsideString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (character == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (character == '"')
                    {
                        isInsideString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    isInsideString = true;
                }
                else if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        endIndex = index;
                        return true;
                    }
                }
            }

            endIndex = -1;
            return false;
        }

        private static void Validate(DiagnosticResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Summary))
            {
                throw new DiagnosticResultParsingException(
                    "诊断结果缺少 Summary。");
            }

            if (string.IsNullOrWhiteSpace(result.RootCause))
            {
                throw new DiagnosticResultParsingException(
                    "诊断结果缺少 RootCause。");
            }

            if (!double.IsFinite(result.Confidence) ||
                result.Confidence < 0 ||
                result.Confidence > 1)
            {
                throw new DiagnosticResultParsingException(
                    "Confidence 必须在 0 到 1 之间。");
            }

            if (!Enum.IsDefined(result.RiskLevel))
            {
                throw new DiagnosticResultParsingException(
                    "RiskLevel 包含无效值。");
            }

            if (result.Evidence is null)
            {
                throw new DiagnosticResultParsingException(
                    "诊断结果缺少 Evidence。");
            }

            if (result.Recommendations is null)
            {
                throw new DiagnosticResultParsingException(
                    "诊断结果缺少 Recommendations。");
            }

            foreach (var evidence in result.Evidence)
            {
                if (!Enum.IsDefined(evidence.Kind) ||
                    string.IsNullOrWhiteSpace(evidence.Description))
                {
                    throw new DiagnosticResultParsingException(
                        "Evidence 包含无效类型或空描述。");
                }
            }

            foreach (var recommendation in result.Recommendations)
            {
                if (string.IsNullOrWhiteSpace(recommendation.Action) ||
                    string.IsNullOrWhiteSpace(recommendation.Reason) ||
                    !Enum.IsDefined(recommendation.RiskLevel))
                {
                    throw new DiagnosticResultParsingException(
                        "Recommendation 必须包含 Action、Reason 和有效 RiskLevel。");
                }
            }
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter(
                namingPolicy: null,
                allowIntegerValues: false));
            return options;
        }

        [GeneratedRegex(
            "<think\\b[^>]*>[\\s\\S]*?</think\\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ThinkBlockRegex();
    }
}
