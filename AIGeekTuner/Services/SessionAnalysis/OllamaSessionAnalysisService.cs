using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Services.SessionAnalysis
{
    /// <summary>
    /// 生产实现：structured output（完整 JSON Schema）优先；解析失败允许一次 repair（§23）；
    /// HTTP/timeout/cancel 不做 repair。Context 超长时结构化裁剪（§20 + V2-M4.3 Gate J）：
    /// 先裁遥测事件（统计永不动），incidents 已由 reducer ≤20，仍超限再从尾部减少 incidents。
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

        private static readonly JsonSerializerOptions ContextJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly IOllamaChatClient _client;
        private readonly ISessionAnalysisPromptBuilder _promptBuilder;
        private readonly SessionAnalysisJsonParser _parser = new();
        private readonly int _maxContextCharacters;
        private readonly Func<string>? _modelNameProvider;

        /// <summary>
        /// V2-M5.1B（Gate D/K）：每次分析开始时调用一次，捕获当次分析的不可变运行时快照；
        /// 返回 null 表示当前没有可用的 AI 服务提供方（不重试、不 repair）。null 工厂 = legacy 路径。
        /// </summary>
        private readonly Func<Task<AiRuntimeSnapshot?>>? _runtimeProvider;

        public OllamaSessionAnalysisService(
            IOllamaChatClient client,
            ISessionAnalysisPromptBuilder promptBuilder,
            int? maxContextCharacters = null,
            Func<string>? modelNameProvider = null,
            Func<Task<AiRuntimeSnapshot?>>? runtimeProvider = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
            _maxContextCharacters = maxContextCharacters ?? DefaultMaxContextCharacters;
            _modelNameProvider = modelNameProvider;
            _runtimeProvider = runtimeProvider;
        }

        /// <summary>V2-M4.3 主入口：组合证据上下文（telemetry + 可选 incidents）→ AI。</summary>
        public async Task<SessionAnalysisRun> AnalyzeAsync(
            DiagnosticEvidenceContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            var stopwatch = Stopwatch.StartNew();

            // Gate J 顺序：遥测先压缩（只裁事件，不动统计）→ incidents 已 ≤20 → 仍超限再减 incidents。
            var telemetry = TrimContextIfNeeded(context.Telemetry, _maxContextCharacters);
            var combined = context with { Telemetry = telemetry };
            while (combined.WindowsIncidents is { Incidents.Count: > 0 }
                && JsonSerializer.Serialize(combined, ContextJsonOptions).Length > _maxContextCharacters)
            {
                var current = combined.WindowsIncidents;
                var keep = current.Incidents.Count - 1;
                combined = combined with
                {
                    WindowsIncidents = current with
                    {
                        Incidents = current.Incidents.Take(keep).ToArray(),
                        IncludedIncidentCount = keep,
                        OmittedIncidentCount = current.TotalIncidentCount - keep,
                    },
                };
            }

            var contextJson = JsonSerializer.Serialize(combined, ContextJsonOptions);
            var validEvidenceIds = CollectValidEvidenceIds(combined);
            var system = _promptBuilder.BuildSystemPrompt();
            var user = _promptBuilder.BuildUserPrompt(combined);

            // Gate D：一次分析只捕获一次运行时快照（第一次请求与 repair 共用）。
            AiRuntimeSnapshot? runtime = null;
            if (_runtimeProvider is not null)
            {
                runtime = await _runtimeProvider().ConfigureAwait(false);
                if (runtime is null)
                {
                    return new SessionAnalysisRun(
                        false, null,
                        "尚未配置可用的 AI 服务提供方，请前往设置。",
                        [], 0, stopwatch.Elapsed, "(unavailable)", false,
                        contextJson);
                }
            }

            return await AnalyzeCoreAsync(system, user, validEvidenceIds, contextJson, stopwatch, runtime, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>兼容入口（M3 语义）：telemetry-only，等价于无 Windows incident 证据的组合上下文。</summary>
        public Task<SessionAnalysisRun> AnalyzeAsync(
            TelemetrySessionAnalyzer.TelemetryAnalysisContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            var trimmed = TrimContextIfNeeded(context, _maxContextCharacters);
            return AnalyzeAsync(
                DiagnosticEvidenceContextBuilder.Build(trimmed, incidents: null),
                cancellationToken);
        }

        private static IReadOnlyList<string> CollectValidEvidenceIds(DiagnosticEvidenceContext context)
        {
            var ids = new List<string>(
                context.Telemetry.Statistics.Count + context.Telemetry.Events.Count + 8);
            ids.AddRange(context.Telemetry.Statistics.Select(statistic => statistic.EvidenceId));
            ids.AddRange(context.Telemetry.Events.Select(@event => @event.EvidenceId));
            // Gate F：只有实际发送给模型的 incident EvidenceId 才合法；
            // omitted / 其他 Session 的 incident ID 一律无效（repair 机会与 stat/event 一致）。
            if (context.WindowsIncidents is not null)
            {
                ids.AddRange(context.WindowsIncidents.Incidents.Select(incident => incident.EvidenceId));
            }

            return ids;
        }

        /// <summary>共享请求/repair 核心（§23）：最多两次请求；传输层失败不做 repair。</summary>
        private async Task<SessionAnalysisRun> AnalyzeCoreAsync(
            string system,
            string user,
            IReadOnlyList<string> validEvidenceIds,
            string evidenceContextJson,
            Stopwatch stopwatch,
            AiRuntimeSnapshot? runtime,
            CancellationToken cancellationToken)
        {
            try
            {
                var first = await _client.ChatAsync(runtime, system, user, ResultSchema, think: false, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var result = _parser.Parse(first, validEvidenceIds);
                    return Done(true, result, null, [], 1, false);
                }
                catch (SessionAnalysisParseException parseFailure)
                {
                    var repairUser = user + " " + _promptBuilder.BuildRepairPrompt(parseFailure.Errors);
                    // Gate I：repair 与第一次请求使用同一份运行时快照。
                    var second = await _client.ChatAsync(runtime, system, repairUser, ResultSchema, think: false, cancellationToken)
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
                    stopwatch.Elapsed, "(unavailable)", false, evidenceContextJson,
                    runtime?.ProviderId, runtime?.ProviderDisplayName);
            }

            SessionAnalysisRun Done(bool ok, SessionAnalysisResult? result, string? error,
                IReadOnlyList<string> validationErrors, int requests, bool repairUsed)
                => new(ok, result, error, validationErrors, requests,
                    stopwatch.Elapsed,
                    (runtime?.ModelId ?? _modelNameProvider?.Invoke() ?? "(unavailable)"),
                    repairUsed,
                    evidenceContextJson,
                    runtime?.ProviderId, runtime?.ProviderDisplayName);
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
