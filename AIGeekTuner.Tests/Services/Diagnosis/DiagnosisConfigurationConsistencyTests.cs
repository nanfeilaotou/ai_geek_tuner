using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.Safety;
using AIGeekTuner.Tests.Services.AI;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.Diagnosis;

/// <summary>
/// 一次诊断全程只使用一份配置/运行时快照（V2-M5.1B 起：AiRuntimeSnapshot）：
/// readiness、prompt 截断、chat、repair 全部取自同一次捕获；
/// 诊断进行中替换 Provider / 模型 / 设置，进行中的诊断不受影响。
/// </summary>
public class DiagnosisConfigurationConsistencyTests : IDisposable
{
    private const string TagsWithBothModels =
        """
{"models":[{"name":"model-a"},{"name":"model-b"}]}
""";

    private readonly StubOllamaServer _server = new();

    [Fact]
    public async Task ReplaceDuringReadiness_RunningDiagnosisKeepsConfigA_NextUsesConfigB()
    {
        // tags 门控：readiness 尚未返回、prompt/chat 均未开始时替换 Provider 配置。
        var tagsGate = _server.GateRawResponse(TagsWithBothModels);
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        _server.EnqueueTags(TagsWithBothModels); // 第二次诊断的 readiness
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);

        var store = new ScriptedProviderStore(CreateConfiguration(
            id: "ollama",
            baseUrl: "http://primary:11434",
            modelName: "model-a",
            mode: AiStructuredOutputMode.NativeSchema,
            maxLogLength: 1200));
        var runtime = CreateRuntime(store);
        var configuration = CreateDiagnosticConfiguration(maxLogLength: 1200, timeoutSeconds: 30);

        var diagnosisService = new DiagnosisService(
            runtime,
            new DiagnosisPromptBuilder(() => configuration.Input),
            new SafetyGuardService());

        var request = CreateRequest(new string('A', 1300));
        var running = diagnosisService.DiagnoseAsync(
            request, configuration, CancellationToken.None);

        await WaitForAsync(() => _server.RequestUrls.Count >= 1);

        // readiness 在途期间保存新配置（换成 secondary / model-b / PromptOnly）。
        store.Replace(CreateConfiguration(
            id: "ollama",
            baseUrl: "http://secondary:11434",
            modelName: "model-b",
            mode: AiStructuredOutputMode.PromptOnly,
            maxLogLength: 5000));
        tagsGate.Release();

        var outcomeA = await running;

        // 第二次诊断使用新配置（重新捕获快照）。
        var outcomeB = await diagnosisService.DiagnoseAsync(
            request, configuration, CancellationToken.None);

        // ---- 本次诊断全程 Config A ----
        Assert.Contains("primary", _server.RequestUrls[0]);          // readiness
        Assert.Contains("primary", _server.RequestUrls[1]);          // 模型请求

        using var chatBody = JsonDocument.Parse(_server.RequestBodies[1]!);
        Assert.Equal("model-a", chatBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("json", chatBody.RootElement.GetProperty("format").GetString());

        using var userJson = GetUserContextJson(_server.RequestBodies[1]!);
        Assert.True(GetWasTruncated(userJson), "Config A 上限 1200 应截断 1300 字符日志");

        // ---- 下一次诊断 Config B ----（[2]=其 tags，[3]=chat）
        using var nextChat = JsonDocument.Parse(_server.RequestBodies[3]!);
        Assert.Equal("model-b", nextChat.RootElement.GetProperty("model").GetString());
        // PromptOnly 模式：绝不发送 format 字段。
        Assert.False(nextChat.RootElement.TryGetProperty("format", out _));
        Assert.Contains("secondary", _server.RequestUrls[3]);

        // Gate L：结果元数据来自各自的运行时快照。
        Assert.Equal("model-a", outcomeA.ModelName);
        Assert.Equal("model-b", outcomeB.ModelName);
    }

    [Fact]
    public async Task NotConfigured_ThrowsAiUnavailable_WithoutAnyRequest()
    {
        var store = new ScriptedProviderStore(AiProviderConfiguration.Empty);
        var runtime = CreateRuntime(store);

        var service = new DiagnosisService(
            runtime,
            new DiagnosisPromptBuilder(new DiagnosisInputOptions()),
            new SafetyGuardService());

        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest("log"), CreateDiagnosticConfiguration(), CancellationToken.None));

        Assert.Equal(DiagnosisError.AiUnavailable, exception.Error);
        Assert.Contains("尚未配置可用的 AI 服务提供方", exception.Message);
        Assert.Empty(_server.RequestUrls);
    }

    [Fact]
    public async Task OllamaReadiness_ModelMissing_ThrowsAiUnavailable_WithoutChatCall()
    {
        var store = new ScriptedProviderStore(CreateConfiguration(modelName: "model-a"));
        var runtime = CreateRuntime(store);

        // tags 返回空模型列表 → readiness ModelMissing。
        _server.EnqueueTags("""
{"models":[{"name":"other-model"}]}
""");

        var service = new DiagnosisService(
            runtime,
            new DiagnosisPromptBuilder(new DiagnosisInputOptions()),
            new SafetyGuardService());

        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest("log"), CreateDiagnosticConfiguration(), CancellationToken.None));

        Assert.Equal(DiagnosisError.AiUnavailable, exception.Error);
        // 只有 readiness 请求（/api/tags），没有 chat 请求。
        Assert.Single(_server.RequestUrls);
        Assert.Contains("api/tags", _server.RequestUrls[0]);
    }

    [Fact]
    public async Task CancelledDuringReadiness_DoesNotContinueDiagnosis()
    {
        using var cts = new CancellationTokenSource();
        var store = new ScriptedProviderStore(CreateConfiguration());
        var runtime = CreateRuntime(store);

        // tags 响应被门控：在取消后释放。
        var gate = _server.GateRawResponse(TagsWithBothModels);
        var service = new DiagnosisService(
            runtime,
            new DiagnosisPromptBuilder(new DiagnosisInputOptions()),
            new SafetyGuardService());

        var running = service.DiagnoseAsync(
            CreateRequest("log"), CreateDiagnosticConfiguration(), cts.Token);
        cts.Cancel();
        gate.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running);

        Assert.Single(_server.RequestUrls);
    }

    private AiChatRuntime CreateRuntime(ScriptedProviderStore store)
    {
        var snapshotSource = new AiRuntimeSnapshotSource(
            store,
            new FakeAiCredentialStore());
        var dispatcher = new AiChatTransportDispatcher(
            new OllamaNativeChatTransport(_server.CreateClient()),
            new OpenAiCompatibleChatTransport(_server.CreateClient()));
        return new AiChatRuntime(
            snapshotSource,
            dispatcher,
            _server.CreateConnectionService());
    }

    private static AiProviderConfiguration CreateConfiguration(
        string id = "ollama",
        string baseUrl = "http://primary:11434",
        string modelName = "model-a",
        AiStructuredOutputMode mode = AiStructuredOutputMode.NativeSchema,
        int maxLogLength = 1200)
    {
        return new AiProviderConfiguration
        {
            Version = 1,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = id,
                    DisplayName = "测试 Ollama",
                    Kind = AiProviderKind.OllamaNative,
                    BaseUrl = baseUrl,
                    Models = [new AiProviderModel(modelName)],
                    DefaultModelId = modelName,
                    StructuredOutputMode = mode,
                    Enabled = true
                }
            ]
        };
    }

    private static DiagnosticConfiguration CreateDiagnosticConfiguration(
        int maxLogLength = 1200,
        int timeoutSeconds = 30)
    {
        return new DiagnosticConfiguration(
            new OllamaOptions { TimeoutSeconds = timeoutSeconds },
            new DiagnosisInputOptions { MaxFaultLogCharacters = maxLogLength });
    }

    private static DiagnosticRequest CreateRequest(string log) =>
        new()
        {
            Hardware = new HardwareInfo
            {
                CpuName = "TEST",
                DetectedAt = DateTimeOffset.UtcNow
            },
            FaultLog = FaultLog.FromPastedText(log)
        };

    /// <summary>chat 请求体 messages[1].content 内嵌一层 JSON 用户上下文。</summary>
    private static JsonDocument GetUserContextJson(string chatRequestBody)
    {
        using var body = JsonDocument.Parse(chatRequestBody);
        var userContent = body.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        return JsonDocument.Parse(userContent[userContent.IndexOf('{')..]);
    }

    private static bool GetWasTruncated(JsonDocument userContext)
    {
        return userContext.RootElement.GetProperty("faultLog")
            .GetProperty("wasTruncated").GetBoolean();
    }

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
