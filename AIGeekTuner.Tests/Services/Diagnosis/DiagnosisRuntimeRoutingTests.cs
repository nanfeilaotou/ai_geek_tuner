using System.Net;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.History;
using AIGeekTuner.Services.Safety;
using AIGeekTuner.Tests.Services.AI;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.Diagnosis;

/// <summary>
/// V2-M5.1B Gate S：Diagnosis 走 Active Provider runtime 的回归。
/// Ollama Native / OpenAI Compatible 都能完成诊断；repair 只在解析失败时发生
/// 且使用与第一次完全相同的运行时快照；HTTP/超时/取消/认证失败一律不做 repair。
/// </summary>
public class DiagnosisRuntimeRoutingTests : IDisposable
{
    private readonly StubOllamaServer _server = new();

    [Fact]
    public async Task OllamaNativeActive_DiagnosisSucceeds_WithRuntimeMetadata()
    {
        var store = CreateStore(kind: AiProviderKind.OllamaNative,
            baseUrl: "http://ollama.local:11434", modelId: "qwen3:8b",
            mode: AiStructuredOutputMode.NativeSchema, activeId: "ollama");
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);

        var service = CreateService(store);
        var outcome = await service.DiagnoseAsync(
            CreateRequest(), CreateConfiguration(), CancellationToken.None);

        Assert.Equal("qwen3:8b", outcome.ModelName);
        Assert.Equal("ollama", outcome.ProviderId);
        Assert.Equal("测试 Ollama", outcome.ProviderDisplayName);
        // 请求确实发往 Ollama 原生端点。
        Assert.Contains("api/chat", _server.RequestUrls[1]);
    }

    [Fact]
    public async Task OpenAiCompatibleActive_DiagnosisSucceeds()
    {
        var store = CreateStore(kind: AiProviderKind.OpenAiCompatible,
            baseUrl: "http://lmstudio.local:1234/v1", modelId: "qwen2.5-7b-instruct",
            mode: AiStructuredOutputMode.OpenAiJsonSchema, activeId: "lmstudio");
        // OpenAI 兼容响应的 content 是一段合法的诊断 JSON。
        var validJson = StubOllamaServer.ValidDiagnosticJson.Replace("\r", " ").Replace("\n", " ");
        var prefix = """
{"choices":[{"message":{"role":"assistant","content":
""";
        var payload = prefix + JsonSerializer.Serialize(validJson) + "}}]}";
        _server.EnqueueHttpStatus(HttpStatusCode.OK, payload);

        var service = CreateService(store);
        var outcome = await service.DiagnoseAsync(
            CreateRequest(), CreateConfiguration(), CancellationToken.None);

        Assert.Equal("qwen2.5-7b-instruct", outcome.ModelName);
        Assert.Equal("lmstudio", outcome.ProviderId);
        // OpenAI 兼容：不做 /models 预检——第一次请求就是 chat/completions。
        Assert.Single(_server.RequestUrls);
        Assert.Contains("chat/completions", _server.RequestUrls[0]);
        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.Equal("json_object", body.RootElement
            .GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ParseFailure_TriggersSingleRepair_WithSameSnapshot()
    {
        var store = CreateStore(modelId: "qwen3:8b");
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        _server.EnqueueTextResponse("这不是 JSON");
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);

        var service = CreateService(store);
        var outcome = await service.DiagnoseAsync(
            CreateRequest(), CreateConfiguration(), CancellationToken.None);

        Assert.Equal(2, _server.RequestUrls.Count - 1); // tags + chat + repair
        // Gate I：repair 与第一次请求同一个模型（同一快照）。
        using var first = JsonDocument.Parse(_server.RequestBodies[1]!);
        using var repair = JsonDocument.Parse(_server.RequestBodies[2]!);
        Assert.Equal(
            first.RootElement.GetProperty("model").GetString(),
            repair.RootElement.GetProperty("model").GetString());
        // repair 请求包含上一条 assistant 回复与修复指令。
        var repairMessages = repair.RootElement.GetProperty("messages");
        Assert.True(repairMessages.GetArrayLength() >= 4);
        Assert.Contains("未能通过 JSON 结构校验",
            repairMessages[repairMessages.GetArrayLength() - 1].GetProperty("content").GetString());
        Assert.NotNull(outcome);
    }

    [Fact]
    public async Task SecondParseFailure_StopsWithoutThirdChat()
    {
        var store = CreateStore();
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        _server.EnqueueTextResponse("bad one");
        _server.EnqueueTextResponse("bad two");

        var service = CreateService(store);
        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest(), CreateConfiguration(), CancellationToken.None));

        Assert.Equal(DiagnosisError.AiResponseInvalid, exception.Error);
        Assert.Equal(3, _server.RequestUrls.Count); // tags + 2 chat，绝无第 3 次 chat
    }

    [Fact]
    public async Task Http500_NoRepair()
    {
        var store = CreateStore();
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        _server.EnqueueHttpStatus(HttpStatusCode.InternalServerError, "boom");

        var service = CreateService(store);
        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest(), CreateConfiguration(), CancellationToken.None));

        Assert.Equal(DiagnosisError.AiRequestFailed, exception.Error);
        Assert.Equal(2, _server.RequestUrls.Count); // tags + 1 chat，无 repair
    }

    [Fact]
    public async Task AuthenticationFailure_NoRepair()
    {
        var store = CreateStore(kind: AiProviderKind.OpenAiCompatible,
            baseUrl: "https://api.deepseek.example", modelId: "deepseek-chat",
            mode: AiStructuredOutputMode.OpenAiJsonSchema, activeId: "deepseek");
        _server.EnqueueHttpStatus(HttpStatusCode.Unauthorized, "{}");

        var service = CreateService(store);
        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest(), CreateConfiguration(), CancellationToken.None));

        Assert.Equal(DiagnosisError.AiRequestFailed, exception.Error);
        Assert.Contains("认证失败", exception.Message);
        Assert.Single(_server.RequestUrls); // 无预检、无 repair
    }

    [Fact]
    public async Task Timeout_NoRepair()
    {
        var store = CreateStore();
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        _server.EnqueueDelayedTextResponse("late", TimeSpan.FromSeconds(2));

        var service = CreateService(store);
        // 快照超时 1 秒 < 响应延迟 2 秒 → 超时不做 repair。
        var configuration = new DiagnosticConfiguration(
            new OllamaOptions { TimeoutSeconds = 1 },
            new DiagnosisInputOptions { MaxFaultLogCharacters = 5000 });
        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest(), configuration, CancellationToken.None));

        Assert.Contains("超时", exception.Message);
        Assert.Equal(2, _server.RequestUrls.Count); // tags + 1 chat，无 repair
    }

    [Fact]
    public async Task Cancellation_NoRepair()
    {
        var store = CreateStore();
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        var gate = _server.GateResponseBody("late response");
        using var cts = new CancellationTokenSource();

        var service = CreateService(store);
        var running = service.DiagnoseAsync(
            CreateRequest(), CreateConfiguration(), cts.Token);

        await WaitForAsync(() => _server.RequestUrls.Count >= 2);
        cts.Cancel();
        gate.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(2, _server.RequestUrls.Count); // 无 repair
    }

    [Fact]
    public async Task SafetyGuard_StillRunsOnRoutedResult()
    {
        var store = CreateStore();
        _server.EnqueueTags("""
{"models":[{"name":"qwen3:8b"}]}
""");
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);

        var service = CreateService(store);
        var outcome = await service.DiagnoseAsync(
            CreateRequest(), CreateConfiguration(), CancellationToken.None);

        Assert.NotNull(outcome.Safety);
        Assert.Equal(SafetyStatus.Approved, outcome.Safety.Status);
    }

    [Fact]
    public async Task HistoryMetadata_ModelNameAndProviderNamePersisted_NoSecrets()
    {
        using var temp = new TempDirectory();
        var historyService = new LocalDiagnosisHistoryService(temp.Combine("history"));
        var outcome = new DiagnosisOutcome
        {
            Request = CreateRequest(),
            AiResult = CannedResult(),
            Safety = new SafetyResult { Status = SafetyStatus.Approved },
            ModelName = "deepseek-chat",
            ProviderId = "deepseek",
            ProviderDisplayName = "DeepSeek V4 Flash"
        };

        var record = await historyService.SaveSuccessAsync(
            outcome, outcome.ModelName!, 1234, CancellationToken.None, outcome.ProviderDisplayName);

        Assert.Equal("deepseek-chat", record.ModelName);
        Assert.Equal("DeepSeek V4 Flash", record.ProviderName);
        var detailJson = System.IO.File.ReadAllText(record.DiagnosisOutcomePath);
        Assert.Contains("deepseek-chat", detailJson);
        Assert.Contains("DeepSeek V4 Flash", detailJson);
        Assert.DoesNotContain("sk-", detailJson);      // Gate L：凭据绝不入库
        Assert.DoesNotContain("apiKey", detailJson, StringComparison.OrdinalIgnoreCase);
    }

    private DiagnosisService CreateService(ScriptedProviderStore store)
    {
        var snapshotSource = new AiRuntimeSnapshotSource(store, new FakeAiCredentialStore());
        var dispatcher = new AiChatTransportDispatcher(
            new OllamaNativeChatTransport(_server.CreateClient()),
            new OpenAiCompatibleChatTransport(_server.CreateClient()));
        var runtime = new AiChatRuntime(snapshotSource, dispatcher, _server.CreateConnectionService());
        return new DiagnosisService(
            runtime,
            new DiagnosisPromptBuilder(new DiagnosisInputOptions()),
            new SafetyGuardService());
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

    private static DiagnosticConfiguration CreateConfiguration() =>
        new(
            new OllamaOptions { TimeoutSeconds = 30 },
            new DiagnosisInputOptions { MaxFaultLogCharacters = 5000 });

    private static DiagnosticRequest CreateRequest() =>
        new()
        {
            Hardware = new HardwareInfo
            {
                CpuName = "TEST CPU",
                DetectedAt = DateTimeOffset.UtcNow
            },
            FaultLog = FaultLog.FromPastedText("E1: sensor fault")
        };

    internal static DiagnosticResult CannedResult() =>
        new()
        {
            Summary = "当前迹象更符合内存稳定性问题，仍需复测确认",
            RootCause = "已有证据：TM5 报错；不确定因素：缺少复测数据。无法确定，需要进一步测试",
            Confidence = 0.55,
            RiskLevel = DiagnosticRiskLevel.Medium,
            Evidence =
            [
                new DiagnosticEvidence { Kind = EvidenceKind.Fact, Description = "日志事实" }
            ],
            Recommendations =
            [
                new Recommendation { Action = "复测", Reason = "验证", RiskLevel = DiagnosticRiskLevel.Low }
            ]
        };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("等待条件超时");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose() => _server.Dispose();
}
