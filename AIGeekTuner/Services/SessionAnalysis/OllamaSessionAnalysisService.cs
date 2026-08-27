using System.Diagnostics;
using System.Text.Json;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 生产实现：structured output（完整 JSON Schema）优先；解析失败允许一次 repair（§23）；
    /// HTTP/timeout/cancel 不做 repair。Context 超长时结构化裁剪事件数量（§20）。
    /// </summary>
    public sealed class OllamaSessionAnalysisService : ISessionAnalysisService
    {
        public const int DefaultMaxContextCharacters = 50_000;
        public const int MinEventsAfterTrim = 5;

        private const string ResultSchema = """
            {
              "type": "object",
              "properties": {
                "summary": { "type": "string" },
                "overallAssessment": { "type": "string", "enum": ["Normal","Attention","PotentialIssue","InsufficientData"] },
                "confidence": { "type": "number" },
                "findings": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "title": { "type": "string" },
                      "category": { "type": "string", "enum": ["Thermal","Performance","Power","Clock","Memory","Storage","Stability","DataQuality","Other"] },
                      "assessment": { "type": "string" },
                      "explanation": { "type": "string" },
                      "evidenceIds": { "type": "array", "items": { "type": "string" } }
                    },
                    "required": ["title","category","assessment","explanation","evidenceIds"]
                  }
                },
                "recommendations": { "type": "array", "items": { "type": "string" } },
                "uncertainties": { "type": "array", "items": { "type": "string" } },
                "spokenSummary": { "type": "string" }
              },
              "required": ["summary","overallAssessment","confidence","findings","recommendations","uncertainties","spokenSummary"]
            }
            """;

        private readonly IOllamaChatClient _client;
        private readonly ISessionAnalysisPromptBuilder _promptBuilder;
        private readonly SessionAnalysisJsonParser _parser = new();
        private readonly int _maxContextCharacters;
        private readonly Func<string>? _modelNameProvider;

        public OllamaSessionAnalysisService(
            IOllamaChatClient client,
            ISessionAnalysisPromptBuilder promptBuilder,
            int? maxContextCharacters = null,
            Func<string>? modelNameProvider = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
            _maxContextCharacters = maxContextCharacters ?? DefaultMaxContextCharacters;
            _modelNameProvider = modelNameProvider;
        }

        public async Task<SessionAnalysisRun> AnalyzeAsync(
            TelemetrySessionAnalyzer.TelemetryAnalysisContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            var stopwatch = Stopwatch.StartNew();

            var trimmed = TrimContextIfNeeded(context, _maxContextCharacters);
            var validEvidenceIds = trimmed.Statistics.Select(s => s.EvidenceId)
                .Concat(trimmed.Events.Select(e => e.EvidenceId))
                .ToArray();
            var system = _promptBuilder.BuildSystemPrompt();
            var user = _promptBuilder.BuildUserPrompt(trimmed);

            try
            {
                var first = await _client.ChatAsync(system, user, ResultSchema, think: false, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var result = _parser.Parse(first, validEvidenceIds);
                    return Done(true, result, null, [], 1, false);
                }
                catch (SessionAnalysisParseException parseFailure)
                {
                    var repairUser = user + " " + _promptBuilder.BuildRepairPrompt(parseFailure.Errors);
                    var second = await _client.ChatAsync(system, repairUser, ResultSchema, think: false, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        var repaired = _parser.Parse(second, validEvidenceIds);
                        return Done(true, repaired, null, [], 2, true);
                    }
                    catch (SessionAnalysisParseException stillBroken)
                    {
                        return Done(false, null,
                            "模型输出两次校验失败。", stillBroken.Errors, 2, true);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // HTTP / 超时等传输层失败不做 repair（§23）。
                return new SessionAnalysisRun(false, null, exception.Message, [], 1,
                    stopwatch.Elapsed, "(unavailable)", false);
            }

            SessionAnalysisRun Done(bool ok, SessionAnalysisResult? result, string? error,
                IReadOnlyList<string> validationErrors, int requests, bool repairUsed)
                => new(ok, result, error, validationErrors, requests,
                    stopwatch.Elapsed, (_modelNameProvider?.Invoke() ?? "(unavailable)"), repairUsed);
        }

        internal static TelemetrySessionAnalyzer.TelemetryAnalysisContext TrimContextIfNeeded(
            TelemetrySessionAnalyzer.TelemetryAnalysisContext context,
            int maxCharacters)
        {
            var current = context;
            while (JsonSerializer.Serialize(current).Length > maxCharacters
                && current.Events.Count > MinEventsAfterTrim)
            {
                var keep = Math.Max(MinEventsAfterTrim, current.Events.Count / 2);
                current = current with { Events = current.Events.Take(keep).ToArray() };
            }

            return current;
        }
    }
}






