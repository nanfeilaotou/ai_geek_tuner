using System.Text.Json;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Telemetry.Recording;
using Xunit;

namespace AIGeekTuner.Tests.Services.SessionAnalysis
{
    /// <summary>
    /// Gate M（prompt 回归，确定性断言）+ Gate F/I/J（incident EvidenceId 校验、
    /// repair 语义、上下文限界）。全部用 fake chat client，不调真实 Ollama/事件日志。
    /// </summary>
    public sealed class IncidentEvidenceAiGroundingTests
    {
        private static readonly DateTimeOffset T0 =
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

        // ---- Gate M：system prompt 核心规则（deterministic assertion） ----

        [Fact]
        public void SystemPrompt_Contains_CorrelationNotCausation_Rule()
        {
            var prompt = new SessionAnalysisPromptBuilder().BuildSystemPrompt();

            Assert.Contains("correlation does not establish causation", prompt);
            Assert.Contains("temporal correlation", prompt);
        }

        [Fact]
        public void SystemPrompt_KernelPower_IsNotPowerFault()
        {
            var prompt = new SessionAnalysisPromptBuilder().BuildSystemPrompt();

            Assert.Contains("Kernel-Power 41", prompt);
            Assert.Contains("不能单独推断 PSU", prompt);
        }

        [Fact]
        public void SystemPrompt_Whea_DoesNotIdentifyBrokenPart()
        {
            var prompt = new SessionAnalysisPromptBuilder().BuildSystemPrompt();

            Assert.Contains("WHEA", prompt);
            Assert.Contains("不得超出事件内容推断具体损坏部件", prompt);
        }

        [Fact]
        public void SystemPrompt_NoIncidents_NoFabrication()
        {
            var prompt = new SessionAnalysisPromptBuilder().BuildSystemPrompt();

            Assert.Contains("绝不得编造任何 Windows 事件", prompt);
        }

        // ---- user prompt：组合上下文分节 ----

        private static TelemetrySessionAnalyzer.TelemetryAnalysisContext Telemetry() =>
            new(
                new TelemetrySessionAnalyzer.AnalysisMetadata("sid", T0, TimeSpan.FromSeconds(30), 15, 2000),
                [new TelemetrySessionAnalyzer.AnalysisSource("HwInfo", "Ready", 3)],
                [new TelemetrySessionAnalyzer.AnalysisStatistic(
                    "stat:cpu:cpu.package.temperature", "cpu", "cpu.package.temperature", "Celsius",
                    15, 100, 70, 72, 74, 74)],
                []);

        private static WindowsIncident Incident(string id, DateTimeOffset at) => new(
            OccurredAtUtc: at,
            Category: IncidentCategory.UnexpectedShutdown,
            Severity: IncidentSeverity.Critical,
            ProviderName: "Microsoft-Windows-Kernel-Power",
            EventId: 41,
            Channel: "System",
            RecordId: 42,
            Summary: "系统未经正常关机就重新启动",
            Details: null,
            EvidenceId: id);

        [Fact]
        public void UserPrompt_IncidentSection_Lists_Evidence_Relations_And_Coverage()
        {
            var envelope = new SessionIncidentEnvelope(
                1, "sid", T0.AddMinutes(1), T0.AddSeconds(-30), T0.AddSeconds(60),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
                IncidentQueryStatus.Partial,
                [new IncidentChannelResult("System", IncidentQueryStatus.Success, null, 3)],
                [
                    Incident("incident:0001", T0.AddSeconds(-10)),
                    Incident("incident:0002", T0.AddSeconds(10)),
                    Incident("incident:0003", T0.AddSeconds(35)),
                ]);
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), envelope);

            var prompt = new SessionAnalysisPromptBuilder().BuildUserPrompt(context);

            Assert.Contains("[WINDOWS INCIDENT EVIDENCE]", prompt);
            Assert.Contains("coverage=Partial", prompt);
            Assert.Contains("incident:0001", prompt);
            Assert.Contains("incident:0003", prompt);
            Assert.Contains("BeforeSession", prompt);
            Assert.Contains("WithinSession", prompt);
            Assert.Contains("AfterSession", prompt);
            Assert.Contains("Microsoft-Windows-Kernel-Power 41", prompt);
        }

        [Fact]
        public void UserPrompt_TelemetryOnly_Declares_NoIncidentEvidence()
        {
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), incidents: null);

            var prompt = new SessionAnalysisPromptBuilder().BuildUserPrompt(context);

            Assert.Contains("[WINDOWS INCIDENT EVIDENCE]", prompt);
            Assert.Contains("none", prompt);
            Assert.Contains("不得编造任何 Windows 事件", prompt);
            Assert.DoesNotContain("incident:", prompt.Replace("incident:XXXX", string.Empty));
        }

        // ---- Gate F/I：service 级 evidence 校验与 repair ----

        private sealed class FakeChat(Func<int, string> respond) : IOllamaChatClient
        {
            public int Requests { get; private set; }
            public List<string> Users { get; } = [];
            public Task<string> ChatAsync(AiRuntimeSnapshot? runtime, string systemPrompt, string userPrompt,
                string? formatJsonSchema, bool? think, CancellationToken cancellationToken)
            {
                Requests++;
                Users.Add(userPrompt);
                return Task.FromResult(respond(Requests));
            }
        }

        private static string ResultJson(params string[] evidenceIds) =>
            "{\"summary\":\"结合遥测与 Windows 事件证据的综合说明，事件与遥测仅时间相关。\","
            + "\"overallAssessment\":\"Attention\",\"confidence\":0.7,"
            + "\"findings\":[{\"title\":\"记录到非正常关机\",\"category\":\"Stability\","
            + "\"assessment\":\"时间相关\",\"explanation\":\"Windows 记录到事件，因果不明。\","
            + "\"evidenceIds\":[" + string.Join(",", evidenceIds.Select(id => "\"" + id + "\"")) + "]}],"
            + "\"recommendations\":[\"复测观察\"],\"uncertainties\":[\"无法判断因果\"],"
            + "\"spokenSummary\":\"本次会话记录到一次非正常关机事件，与遥测仅时间相关。\"}";

        private static SessionIncidentEnvelope Envelope(IReadOnlyList<WindowsIncident> incidents) => new(
            SchemaVersion: 1,
            SessionId: "sid",
            QueriedAtUtc: T0.AddMinutes(1),
            WindowStartUtc: T0.AddSeconds(-30),
            WindowEndUtc: T0.AddSeconds(60),
            PreBuffer: TimeSpan.FromSeconds(30),
            PostBuffer: TimeSpan.FromSeconds(30),
            QueryStatus: IncidentQueryStatus.Success,
            Channels: [new IncidentChannelResult("System", IncidentQueryStatus.Success, null, incidents.Count)],
            Incidents: incidents);

        private static DiagnosticEvidenceContext ContextWithIncidents(
            IReadOnlyList<WindowsIncident> incidents) =>
            DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));

        [Fact]
        public async Task Service_ValidIncidentEvidence_IsAccepted_AndContextJsonRecordsIt()
        {
            var client = new FakeChat(_ => ResultJson("stat:cpu:cpu.package.temperature", "incident:0001"));
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());
            var context = ContextWithIncidents([Incident("incident:0001", T0.AddSeconds(10))]);

            var run = await service.AnalyzeAsync(context, CancellationToken.None);

            Assert.True(run.Success);
            Assert.Equal(1, run.RequestCount);
            Assert.False(run.RepairUsed);
            Assert.False(string.IsNullOrEmpty(run.EvidenceContextJson));
            using var document = JsonDocument.Parse(run.EvidenceContextJson!);
            Assert.Equal("sid", document.RootElement.GetProperty("Metadata").GetProperty("SessionId").GetString());
            Assert.Contains("incident:0001", run.EvidenceContextJson);
        }

        [Fact]
        public async Task Service_UnknownIncidentEvidence_TriggersSingleRepair()
        {
            string[] responses = [ResultJson("incident:9999"), ResultJson("incident:0001")];
            var call = 0;
            var client = new FakeChat(_ => responses[call++]);
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());
            var context = ContextWithIncidents([Incident("incident:0001", T0.AddSeconds(10))]);

            var run = await service.AnalyzeAsync(context, CancellationToken.None);

            Assert.True(run.Success);
            Assert.Equal(2, run.RequestCount);
            Assert.True(run.RepairUsed); // repair 引用真实存在的 incident
            Assert.Equal(2, client.Requests); // 绝无第三次请求
        }

        [Fact]
        public async Task Service_OmittedIncidentEvidence_IsRejected()
        {
            // 6 条 WER → reducer 只保留 4 条（WER cap），incident:0005 被 omitted，
            // AI 引用它必须失败并 repair 到真实发送的 incident:0001。
            var incidents = Enumerable.Range(1, 6).Select(i =>
                new WindowsIncident(
                    OccurredAtUtc: T0.AddSeconds(i),
                    Category: IncidentCategory.WindowsErrorReporting,
                    Severity: IncidentSeverity.Information,
                    ProviderName: "Windows Error Reporting",
                    EventId: 1001,
                    Channel: "Application",
                    RecordId: i,
                    Summary: "WER " + i,
                    Details: null,
                    EvidenceId: $"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}")).ToArray();
            string[] responses = [ResultJson("incident:0005"), ResultJson("incident:0001")];
            var call = 0;
            var client = new FakeChat(_ => responses[call++]);
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(ContextWithIncidents(incidents), CancellationToken.None);

            Assert.True(run.Success);
            Assert.True(run.RepairUsed);
            var prompt = client.Users[0];
            Assert.Contains("incident:0001", prompt);
            Assert.DoesNotContain("incident:0005", prompt); // omitted 不发送
        }

        [Fact]
        public async Task Service_BothResponses_UnknownEvidence_Fails_WithoutThirdRequest()
        {
            var client = new FakeChat(_ => ResultJson("incident:9999"));
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());
            var context = ContextWithIncidents([Incident("incident:0001", T0.AddSeconds(10))]);

            var run = await service.AnalyzeAsync(context, CancellationToken.None);

            Assert.False(run.Success);
            Assert.Equal(2, run.RequestCount); // repair 仍最多一次
        }

        [Fact]
        public async Task Service_CombinedContext_StaysBelow_SizeLimit()
        {
            var incidents = Enumerable.Range(1, 20).Select(i =>
                new WindowsIncident(
                    OccurredAtUtc: T0.AddSeconds(i),
                    Category: IncidentCategory.ApplicationCrash,
                    Severity: IncidentSeverity.Error,
                    ProviderName: "Application Error",
                    EventId: 1000,
                    Channel: "Application",
                    RecordId: i,
                    Summary: "应用崩溃 " + i,
                    Details: new string('x', 2000), // Details 不进 AI：即使存在也不影响
                    EvidenceId: $"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}")).ToArray();
            var client = new FakeChat(_ => ResultJson("incident:0001"));
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(
                ContextWithIncidents(incidents), CancellationToken.None);

            Assert.True(run.Success);
            Assert.NotNull(run.EvidenceContextJson);
            Assert.True(run.EvidenceContextJson!.Length <= OllamaSessionAnalysisService.DefaultMaxContextCharacters,
                $"context was {run.EvidenceContextJson.Length} chars");
        }

        [Fact]
        public async Task Service_OverLimit_TrimsIncidents_ButNeverTelemetryStatistics()
        {
            var statistics = Enumerable.Range(0, 4).Select(i =>
                new TelemetrySessionAnalyzer.AnalysisStatistic(
                    $"stat:cpu:metric{i}", "cpu", "metric" + i, "Celsius", 15, 100, 1, 2, 3, 3)).ToArray();
            var telemetry = new TelemetrySessionAnalyzer.TelemetryAnalysisContext(
                new TelemetrySessionAnalyzer.AnalysisMetadata("sid", T0, TimeSpan.FromSeconds(30), 15, 2000),
                [new TelemetrySessionAnalyzer.AnalysisSource("HwInfo", "Ready", 3)],
                statistics,
                []);
            var incidents = Enumerable.Range(1, 20).Select(i =>
                new WindowsIncident(
                    OccurredAtUtc: T0.AddSeconds(i),
                    Category: IncidentCategory.ApplicationCrash,
                    Severity: IncidentSeverity.Error,
                    ProviderName: "Application Error",
                    EventId: 1000,
                    Channel: "Application",
                    RecordId: i,
                    Summary: new string('s', 300) + " " + i, // 大 Summary 迫使超限
                    Details: null,
                    EvidenceId: $"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}")).ToArray();
            var client = new FakeChat(_ => ResultJson("stat:cpu:metric0"));
            // 小限额：telemetry 事件裁剪不适用（Events 为空），incidents 必须被裁减；
            // 统计 4 条一条不许少。
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder(), maxContextCharacters: 4_000);

            var run = await service.AnalyzeAsync(
                DiagnosticEvidenceContextBuilder.Build(telemetry, Envelope(incidents)), CancellationToken.None);

            Assert.True(run.Success);
            using var document = JsonDocument.Parse(run.EvidenceContextJson!);
            var statisticsElement = document.RootElement.GetProperty("Telemetry").GetProperty("Statistics");
            Assert.Equal(4, statisticsElement.GetArrayLength()); // 统计永不被裁
            var incidentCount = document.RootElement
                .GetProperty("WindowsIncidents").GetProperty("Incidents").GetArrayLength();
            Assert.True(incidentCount < 20, $"incidents were {incidentCount}");
        }

        [Fact]
        public async Task Service_TelemetryOnly_OldSession_AnalyzesNormally()
        {
            var client = new FakeChat(_ => ResultJson("stat:cpu:cpu.package.temperature"));
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(
                DiagnosticEvidenceContextBuilder.Build(Telemetry(), incidents: null), CancellationToken.None);

            Assert.True(run.Success);
            Assert.Contains("[WINDOWS INCIDENT EVIDENCE]", client.Users[0]);
        }
    }
}
