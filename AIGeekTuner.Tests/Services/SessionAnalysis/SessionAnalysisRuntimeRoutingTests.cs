using System.IO;
using System.Net;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Tests.Services.AI;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.SessionAnalysis;

/// <summary>
/// V2-M5.1B Gate T：Session Analysis 走 Active Provider runtime 的回归。
/// 结构化输出 payload、repair 快照一致性、analysis.json 元数据、无 Provider 失败语义。
/// </summary>
public class SessionAnalysisRuntimeRoutingTests : IDisposable
{
    private readonly StubOllamaServer _server = new();

    [Fact]
    public async Task OllamaNativeActive_AnalysisSucceeds_WithRuntimeMetadata()
    {
        var store = CreateStore(kind: AiProviderKind.OllamaNative,
            baseUrl: "http://ollama.local:11434", modelId: "qwen3:8b",
            mode: AiStructuredOutputMode.NativeSchema, activeId: "ollama");
        _server.EnqueueTextResponse(ValidSessionJson);
        var service = CreateService(store);

        var run = await service.AnalyzeAsync(CreateContext(), CancellationToken.None);

        Assert.True(run.Success);
        Assert.Equal("qwen3:8b", run.ModelName);
        Assert.Equal("ollama", run.ProviderId);
        Assert.Equal("测试 Ollama", run.ProviderName);
        // NativeSchema + 完整 session schema → format 是原生 schema 对象。
        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("format").ValueKind);
        Assert.Contains("overallAssessment", _server.RequestBodies[0]);
    }

    [Fact]
    public async Task OpenAiCompatibleActive_AnalysisSucceeds_WithJsonSchemaResponseFormat()
    {
        var store = CreateStore(kind: AiProviderKind.OpenAiCompatible,
            baseUrl: "http://lmstudio.local:1234/v1", modelId: "qwen2.5-7b-instruct",
            mode: AiStructuredOutputMode.OpenAiJsonSchema, activeId: "lmstudio");
        _server.EnqueueHttpStatus(HttpStatusCode.OK, OpenAiBodyWith(ValidSessionJson));
        var service = CreateService(store);

        var run = await service.AnalyzeAsync(CreateContext(), CancellationToken.None);

        Assert.True(run.Success);
        Assert.Equal("qwen2.5-7b-instruct", run.ModelName);
        Assert.Equal("lmstudio", run.ProviderId);
        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        var responseFormat = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        // 现有 session schema 原样进入 json_schema.schema（Gate T-3）。
        Assert.Contains("overallAssessment",
            responseFormat.GetProperty("json_schema").GetProperty("schema").GetRawText());
    }

    [Fact]
    public async Task JsonObjectMode_UsesFormatJson_OnOllama()
    {
        var store = CreateStore(mode: AiStructuredOutputMode.JsonObject);
        _server.EnqueueTextResponse(ValidSessionJson);
        var service = CreateService(store);

        var run = await service.AnalyzeAsync(CreateContext(), CancellationToken.None);

        Assert.True(run.Success);
        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task ParseFailure_RepairUsesSameRuntimeSnapshot()
    {
        var store = CreateStore(modelId: "qwen3:8b");
        _server.EnqueueTextResponse("not json");
        _server.EnqueueTextResponse(ValidSessionJson);
        var service = CreateService(store);

        var run = await service.AnalyzeAsync(CreateContext(), CancellationToken.None);

        Assert.True(run.Success);
        Assert.True(run.RepairUsed);
        Assert.Equal(2, _server.RequestUrls.Count);
        // Gate I：两次请求同一模型、同一端点（同一快照）。
        Assert.Equal(_server.RequestUrls[0], _server.RequestUrls[1]);
        using var first = JsonDocument.Parse(_server.RequestBodies[0]!);
        using var repair = JsonDocument.Parse(_server.RequestBodies[1]!);
        Assert.Equal(
            first.RootElement.GetProperty("model").GetString(),
            repair.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task TransportFailure_DoesNotRepair_ReportsFriendlyError()
    {
        var store = CreateStore();
        _server.EnqueueHttpStatus(HttpStatusCode.InternalServerError, "boom");
        var service = CreateService(store);

        var run = await service.AnalyzeAsync(CreateContext(), CancellationToken.None);

        Assert.False(run.Success);
        Assert.False(run.RepairUsed);
        Assert.Single(_server.RequestUrls);
    }

    [Fact]
    public async Task NotConfigured_FailsWithFriendlyMessage_ZeroRequests()
    {
        var service = CreateService(new ScriptedProviderStore(AiProviderConfiguration.Empty));

        var run = await service.AnalyzeAsync(CreateContext(), CancellationToken.None);

        Assert.False(run.Success);
        Assert.Contains("尚未配置可用的 AI 服务提供方", run.ErrorMessage);
        Assert.Equal(0, run.RequestCount);
        Assert.Empty(_server.RequestUrls);
    }

    [Fact]
    public void EnvelopePersists_ModelIdAndProviderMetadata_NoSecrets()
    {
        using var temp = new TempDirectory();
        var analysisStore = new SessionAnalysisStore(temp.Combine("sessions"));
        var envelope = new SessionAnalysisEnvelope(
            SchemaVersion: 1,
            SessionId: "s-1",
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            ModelName: "qwen2.5-7b-instruct",
            DurationMs: 500,
            RepairUsed: false,
            Result: new SessionAnalysisResult(
                "摘要", SessionOverallAssessment.Normal, 0.8, [], [], [], "语音摘要"),
            ContextJson: "{}",
            ProviderId: "lmstudio",
            ProviderName: "LM Studio");

        analysisStore.Save(envelope);
        var loaded = analysisStore.Load("s-1");

        Assert.NotNull(loaded);
        Assert.Equal("qwen2.5-7b-instruct", loaded.ModelName);
        Assert.Equal("lmstudio", loaded.ProviderId);
        Assert.Equal("LM Studio", loaded.ProviderName);
        // SpokenSummary 字段原样保留（Gate T-11：不改 voice pipeline）。
        Assert.Equal("语音摘要", loaded.Result.SpokenSummary);
        var json = File.ReadAllText(Path.Combine(temp.Combine("sessions"), "s-1", "analysis.json"));
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    private OllamaSessionAnalysisService CreateService(ScriptedProviderStore store)
    {
        var snapshotSource = new AiRuntimeSnapshotSource(store, new FakeAiCredentialStore());
        var dispatcher = new AiChatTransportDispatcher(
            new OllamaNativeChatTransport(_server.CreateClient()),
            new OpenAiCompatibleChatTransport(_server.CreateClient()));
        var chatClient = new OllamaChatClient(
            _server.CreateClient(),
            () => new OllamaOptions(),
            dispatcher);
        return new OllamaSessionAnalysisService(
            chatClient,
            new SessionAnalysisPromptBuilder(),
            runtimeProvider: async () => await snapshotSource.TryCaptureAsync(30));
    }

    private static ScriptedProviderStore CreateStore(
        AiProviderKind kind = AiProviderKind.OllamaNative,
        string baseUrl = "http://ollama.local:11434",
        string modelId = "qwen3:8b",
        AiStructuredOutputMode mode = AiStructuredOutputMode.NativeSchema,
        string activeId = "ollama")
    {
        return new ScriptedProviderStore(new AiProviderConfiguration
        {
            Version = 1,
            ActiveProviderId = activeId,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = activeId,
                    DisplayName = "测试 Ollama",
                    Kind = kind,
                    BaseUrl = baseUrl,
                    Models = [new AiProviderModel(modelId)],
                    DefaultModelId = modelId,
                    StructuredOutputMode = mode,
                    Enabled = true
                }
            ]
        });
    }

    private static DiagnosticEvidenceContext CreateContext()
    {
        const string statId = "stat:cpu:cpu.package.temperature";
        const string eventId = "event:0001";
        var t0 = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var context = new TelemetrySessionAnalyzer.TelemetryAnalysisContext(
            new TelemetrySessionAnalyzer.AnalysisMetadata("sid", t0, TimeSpan.FromSeconds(30), 15, 2000),
            [new TelemetrySessionAnalyzer.AnalysisSource("HwInfo", "Ready", 3)],
            [new TelemetrySessionAnalyzer.AnalysisStatistic(
                statId, "cpu", "cpu.package.temperature", "Celsius",
                15, 100, 70, 72, 74, 74)],
            [
                new TelemetrySessionAnalyzer.AnalysisEvent(
                    eventId, "SignificantChange", t0.AddSeconds(10), "HwInfo",
                    "gpu.core.clock", "2400 MHz", "210 MHz", "clock drop"),
            ]);
        return DiagnosticEvidenceContextBuilder.Build(context, incidents: null);
    }

    private const string ValidSessionJson = """
    {
      "summary": "CPU 温度整体稳定",
      "overallAssessment": "Normal",
      "confidence": 0.82,
      "findings": [
        {
          "title": "温度正常",
          "category": "Thermal",
          "assessment": "无明显异常",
          "explanation": "全窗口温度低于阈值",
          "evidenceIds": ["stat:cpu:cpu.package.temperature"]
        }
      ],
      "recommendations": ["继续观察"],
      "uncertainties": ["采样窗口较短"],
      "spokenSummary": "温度正常"
    }
    """;

    private static string OpenAiBodyWith(string content) =>
        "{\"choices\":[{\"message\":{\"role\":\"assistant\"," +
        "\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

    public void Dispose() => _server.Dispose();
}