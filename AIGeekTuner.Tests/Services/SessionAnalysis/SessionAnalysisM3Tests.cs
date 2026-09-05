using AIGeekTuner.Models.Telemetry;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Net;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Services.Voice;
using AIGeekTuner.Services.Telemetry;
using Xunit;

namespace AIGeekTuner.Tests.Services.SessionAnalysis
{
    /// <summary>V2-M3：解析校验 / repair 矩阵 / 持久化 / Prompt 固件 / GSV 客户端。</summary>
    public class SessionAnalysisM3Tests
    {
        private static readonly DateTimeOffset T0 =
            new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);

        private const string ValidEvidenceA = "stat:cpu:cpu.package.temperature";
        private const string ValidEvidenceB = "event:0001";

        private static readonly string[] EvidenceIds = [ValidEvidenceA, ValidEvidenceB];

        private static string ValidJson(string spoken = "本次录制整体运行稳定，温度与功耗处于常见波动范围。建议保持观察。")
        {
            var findings = "["
                + "{\"title\":\"CPU 温度平稳\",\"category\":\"Thermal\",\"assessment\":\"处于常见范围\",\"explanation\":\"全程 70~74 度小幅波动。\",\"evidenceIds\":[\"" + ValidEvidenceA + "\"]}," 
                + "{\"title\":\"出现一次关键事件\",\"category\":\"Performance\",\"assessment\":\"需要结合场景理解\",\"explanation\":\"录制期间记录到一次显著变化事件。\",\"evidenceIds\":[\"" + ValidEvidenceB + "\"]}" 
                + "]";
            return "{"
                + "\"summary\":\"CPU 温度在 70~74 度间小幅波动，整体平稳，未观察到异常事件。\"," 
                + "\"overallAssessment\":\"Normal\"," 
                + "\"confidence\":0.8," 
                + "\"findings\":" + findings + "," 
                + "\"recommendations\":[\"在不同负载下复测\"]," 
                + "\"uncertainties\":[\"本会话未包含帧时间数据\"]," 
                + "\"spokenSummary\":\"" + spoken + "\"" 
                + "}";
        }

        [Fact]
        public void Parse_EmptyFindings_IsAllowed()
        {
            var json = "{\"summary\":\"s\",\"overallAssessment\":\"Normal\",\"confidence\":0.9,"
                + "\"findings\":[],\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"ok text here\"}";

            var result = new SessionAnalysisJsonParser().Parse(json, EvidenceIds);

            Assert.Empty(result.Findings);
        }

        [Theory]
        [InlineData("Broken")]
        [InlineData("CriticalFailure")]
        public void Parse_InvalidEnum_Fails(string assessment)
        {
            var json = "{\"summary\":\"s\",\"overallAssessment\":\"" + assessment + "\",\"confidence\":0.5,"
                + "\"findings\":[],\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"x\"}";

            Assert.Throws<SessionAnalysisParseException>(() =>
                new SessionAnalysisJsonParser().Parse(json, EvidenceIds));
        }

        [Fact]
        public void Parse_ConfidenceAboveOne_Fails()
        {
            var json = "{\"summary\":\"s\",\"overallAssessment\":\"Normal\",\"confidence\":1.5,"
                + "\"findings\":[],\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"x\"}";

            Assert.Throws<SessionAnalysisParseException>(() =>
                new SessionAnalysisJsonParser().Parse(json, EvidenceIds));
        }

        [Fact]
        public void Parse_UnknownEvidenceId_Rejected()
        {
            var json = "{\"summary\":\"s\",\"overallAssessment\":\"Attention\",\"confidence\":0.5,"
                + "\"findings\":[{\"title\":\"t\",\"category\":\"Other\",\"assessment\":\"a\",\"explanation\":\"e\","
                + "\"evidenceIds\":[\"stat:not:exist\"]}],"
                + "\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"x\"}";

            var ex = Assert.Throws<SessionAnalysisParseException>(() =>
                new SessionAnalysisJsonParser().Parse(json, EvidenceIds));
            Assert.Contains(ex.Errors, e => e.Contains("未知证据 ID"));
        }

        [Fact]
        public void Parse_MissingSummary_Fails()
        {
            var json = "{\"overallAssessment\":\"Normal\",\"confidence\":0.5,"
                + "\"findings\":[],\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"x\"}";

            var ex = Assert.Throws<SessionAnalysisParseException>(() =>
                new SessionAnalysisJsonParser().Parse(json, EvidenceIds));
            Assert.Contains(ex.Errors, e => e.Contains("summary"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("# 标题\n- 列表")]          // Markdown
        public void Parse_BadSpokenSummary_Fails(string spoken)
        {
            var json = "{\"summary\":\"s\",\"overallAssessment\":\"Normal\",\"confidence\":0.5,"
                + "\"findings\":[],\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"" + spoken + "\"}";

            Assert.Throws<SessionAnalysisParseException>(() =>
                new SessionAnalysisJsonParser().Parse(json, EvidenceIds));
        }

        [Fact]
        public void Parse_MalformedJson_Fails()
        {
            Assert.Throws<SessionAnalysisParseException>(() =>
                new SessionAnalysisJsonParser().Parse("{ not json", EvidenceIds));
        }

        // ---- §51 Repair 矩阵 ----

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

        private static TelemetrySessionAnalyzer.TelemetryAnalysisContext Context() =>
            new(
                new TelemetrySessionAnalyzer.AnalysisMetadata("sid", T0, TimeSpan.FromSeconds(30), 15, 2000),
                [new TelemetrySessionAnalyzer.AnalysisSource("HwInfo", "Ready", 3)],
                [new TelemetrySessionAnalyzer.AnalysisStatistic(
                    ValidEvidenceA, "cpu", "cpu.package.temperature", "Celsius",
                    15, 100, 70, 72, 74, 74)],
                [
                    new TelemetrySessionAnalyzer.AnalysisEvent(
                        ValidEvidenceB, "SignificantChange", T0.AddSeconds(10), "HwInfo",
                        "gpu.core.clock", "2400 MHz", "210 MHz", "clock drop"),
                ]);

        [Fact]
        public async Task Repair_InvalidThenValid_TwoRequestsAndRepairUsed()
        {
            var responses = new[] { "{ bad", ValidJson() };
            var call = 0;
            var client = new FakeChat(_ => responses[call++]);
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.True(run.Success);
            Assert.Equal(2, run.RequestCount);
            Assert.True(run.RepairUsed);
        }

        [Fact]
        public async Task Repair_ValidFirst_SingleRequest()
        {
            var client = new FakeChat(_ => ValidJson());
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.True(run.Success);
            Assert.Equal(1, run.RequestCount);
            Assert.False(run.RepairUsed);
        }

        [Fact]
        public async Task Repair_InvalidTwice_FailsWithoutThirdRequest()
        {
            var client = new FakeChat(_ => "{ bad");
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.False(run.Success);
            Assert.Equal(2, run.RequestCount);
        }

        private sealed class ThrowingClient(Exception exception) : IOllamaChatClient
        {
            public int Requests { get; private set; }

            public Task<string> ChatAsync(AiRuntimeSnapshot? runtime, string systemPrompt, string userPrompt,
                string? formatJsonSchema, bool? think, CancellationToken cancellationToken)
            {
                Requests++;
                throw exception;
            }
        }

        [Fact]
        public async Task HttpError_DoesNotRepair()
        {
            var client = new ThrowingClient(new HttpRequestException("500"));
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.False(run.Success);
            Assert.Equal(1, run.RequestCount);
        }

        [Fact]
        public async Task Timeout_DoesNotRepair()
        {
            var client = new ThrowingClient(new TimeoutException());
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.False(run.Success);
            Assert.Equal(1, run.RequestCount);
        }

        [Fact]
        public async Task Cancellation_PropagatesWithoutRepair()
        {
            var client = new ThrowingClient(new OperationCanceledException());
            var service = new OllamaSessionAnalysisService(client, new SessionAnalysisPromptBuilder());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.AnalyzeAsync(Context(), cts.Token));
        }

        [Fact]
        public async Task ModelName_ComesFromSnapshotAtStart()
        {
            string modelName = "model-a";
            var optionsProvider = () => new OllamaOptions { ModelName = modelName };
            var httpClient = new HttpClient(new FakeHttpHandler(async (request, ct) =>
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Assert.Contains("\"model\":\"model-a\"", body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(ValidJson()) + "}}"),
                };
            }));
            var realClient = new OllamaChatClient(httpClient, optionsProvider);
            var service = new OllamaSessionAnalysisService(realClient, new SessionAnalysisPromptBuilder());

            var run = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.True(run.Success);
        }

        private sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => handler(request, cancellationToken);
        }

        // ---- §49 analysis.json 持久化 ----

        [Fact]
        public void AnalysisStore_SaveLoadOverwrite_SessionUnaffected_CorruptSkipped()
        {
            var dir = Path.Combine(Path.GetTempPath(), "agt-an-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SessionAnalysisStore(dir);
                var result = new SessionAnalysisJsonParser().Parse(ValidJson(), EvidenceIds);
                store.Save(new SessionAnalysisEnvelope(1, "sid", T0, "m", 100, false, result, "{}"));

                var loaded = store.Load("sid");
                Assert.NotNull(loaded);
                Assert.Equal("m", loaded.ModelName);
                Assert.Equal(100, loaded.DurationMs);

                store.Save(new SessionAnalysisEnvelope(1, "sid", T0, "m2", 200, true, result, "{}"));
                Assert.Equal("m2", store.Load("sid")!.ModelName); // 覆盖式重分析

                var sessionFile = Path.Combine(dir, "sid", "session.json");
                Assert.False(File.Exists(sessionFile)); // session.json 不被 AI 结果污染（§25）

                Directory.CreateDirectory(Path.Combine(dir, "broken"));
                File.WriteAllText(Path.Combine(dir, "broken", "analysis.json"), "junk");
                Assert.Null(store.Load("broken")); // 损坏 → null 不抛出
                Assert.Null(store.Load("missing"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}








    /// <summary>Gate B 回归：unresolved AIDA GPU 绝不并入具名 NVIDIA 集群。</summary>
    public class DisplayAggregatorSemanticTests
    {
        [Fact]
        public void UnresolvedAidaGpu_NeverMergesIntoNamedNvidiaCluster()
        {
            var snapshot = CreateSnapshotWithUnresolvedAida();
            var views = HardwareTelemetryDisplayAggregator.Build(
                snapshot,
                metric => metric.Value,
                (v, u) => v.ToString("0.#"),
                src => src.ToString());

            // nvidia 集群卡片中不允许出现来源为 Aida64 的行。
            var nvidiaCard = views.FirstOrDefault(v =>
                v.DisplayName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(nvidiaCard);
            Assert.All(nvidiaCard.Metrics, row =>
                Assert.NotEqual("Aida64", row.SourceDisplay));
        }

        private static TelemetrySnapshot CreateSnapshotWithUnresolvedAida()
        {
            var nvidiaDevice = TelemetryDeviceIdentity.GpuByIndex(0, "NVIDIA RTX 4080");
            var aidaGpu = new TelemetryDeviceIdentity(
                TelemetryDeviceKind.Gpu, "src:Aida64:gpu:0", "GPU #1");
            return new TelemetrySnapshot(DateTimeOffset.UtcNow,
            [
                new TelemetryReading(TelemetryMetricKey.GpuCoreTemperature, 60,
                    TelemetryUnit.Celsius, nvidiaDevice,
                    TelemetrySourceKind.LibreHardwareMonitor, "r1", null, DateTimeOffset.UtcNow),
                new TelemetryReading(TelemetryMetricKey.GpuHotspotTemperature, 65,
                    TelemetryUnit.Celsius, aidaGpu,
                    TelemetrySourceKind.Aida64, "r2", null, DateTimeOffset.UtcNow),
            ], [], []);
        }
    }


