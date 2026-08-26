using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.Safety;
using AIGeekTuner.Tests.Services.AI;

namespace AIGeekTuner.Tests.Services.Diagnosis;

/// <summary>
/// Gate A 核心契约：一次诊断从开始到结束只使用一份配置快照，
/// 即便设置在 readiness 阶段被替换；下一次诊断才使用新配置。
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
        // tags 门控：readiness 尚未返回、prompt/chat 均未开始时替换配置。
        var tagsGate = _server.GateRawResponse(TagsWithBothModels);
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        _server.EnqueueTags(TagsWithBothModels); // 第二次诊断的 readiness
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);

        var store = new DiagnosticConfigurationStore(CreateConfiguration(
            baseUrl: "http://primary:11434",
            modelName: "model-a",
            useJsonFormat: true,
            maxLogLength: 1200));

        var diagnosisService = CreateDiagnosisService(store);

        var request = CreateRequest(new string('A', 1300));
        var running = diagnosisService.DiagnoseAsync(
            request, store.Snapshot(), CancellationToken.None);

        await WaitForAsync(() => _server.RequestUrls.Count >= 1);

        // readiness 在途期间保存新配置
        store.Replace(SnapshotFor(
            baseUrl: "http://secondary:11434",
            modelName: "model-b",
            useJsonFormat: false,
            maxLogLength: 5000));
        tagsGate.Release();

        await running;

        // 第二次诊断使用新配置
        await diagnosisService.DiagnoseAsync(
            request, store.Snapshot(), CancellationToken.None);

        // ---- 本次诊断全程 Config A ----
        Assert.Contains("primary", _server.RequestUrls[0]);          // readiness
        Assert.Contains("primary", _server.RequestUrls[1]);          // 模型请求

        using var chatBody = JsonDocument.Parse(_server.RequestBodies[1]!);
        Assert.Equal("model-a", GetModelProperty(chatBody, "model"));
        Assert.Equal("json", GetModelProperty(chatBody, "format"));

        using var userJson = GetUserContextJson(_server.RequestBodies[1]!);
        Assert.True(GetWasTruncated(userJson), "Config A 上限 1200 应截断 1300 字符日志");

        // ---- 下一次诊断 Config B ----（[2]=其 tags，[3]=chat）
        using var nextChat = JsonDocument.Parse(_server.RequestBodies[3]!);
        Assert.Equal("model-b", GetModelProperty(nextChat, "model"));
        Assert.False(nextChat.RootElement.TryGetProperty("format", out _));

        using var nextUser = GetUserContextJson(_server.RequestBodies[3]!);
        Assert.False(GetWasTruncated(nextUser));
    }

    [Fact]
    public async Task Ready_ProceedsAndPassesSameSnapshotOptions()
    {
        _server.EnqueueTags(TagsWithBothModels);
        _server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var fakeAi = new RecordingFakeAiService();
        var readiness = new StaticFakeReadiness(AiReadinessStatus.Ready);
        var service = CreateUnitDiagnosisService(fakeAi, readiness);
        var configuration = CreateConfiguration();

        await service.DiagnoseAsync(
            CreateRequest("log"), configuration, CancellationToken.None);

        var options = Assert.Single(fakeAi.CallsWith);
        Assert.Same(configuration.Ollama, options);
        var checkedOptions = Assert.Single(readiness.CheckCalls);
        Assert.Same(configuration.Ollama, checkedOptions);
    }

    [Fact]
    public async Task Offline_ThrowsOllamaUnavailable_WithoutChatCall()
    {
        var fakeAi = new RecordingFakeAiService();
        var readiness = new StaticFakeReadiness(
            AiReadinessStatus.Unavailable,
            "Ollama 服务未启动或无法连接。");
        var service = CreateUnitDiagnosisService(fakeAi, readiness);

        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest("log"), CreateConfiguration(), CancellationToken.None));

        Assert.Equal(DiagnosisError.OllamaUnavailable, exception.Error);
        Assert.Contains("未启动或无法连接", exception.Message);
        Assert.Empty(fakeAi.CallsWith);
    }

    [Fact]
    public async Task ModelMissing_MessageContainsModelName_WithoutChatCall()
    {
        var fakeAi = new RecordingFakeAiService();
        var readiness = new StaticFakeReadiness(
            AiReadinessStatus.ModelMissing,
            "已连接 Ollama，但未找到模型 gemma2:9b。请先安装或在设置中选择已安装模型。");
        var service = CreateUnitDiagnosisService(fakeAi, readiness);

        var configuration = CreateConfiguration(modelName: "gemma2:9b");
        var exception = await Assert.ThrowsAsync<DiagnosisException>(
            () => service.DiagnoseAsync(
                CreateRequest("log"), configuration, CancellationToken.None));

        Assert.Contains("gemma2:9b", exception.Message);
        Assert.Empty(fakeAi.CallsWith);
    }

    [Fact]
    public async Task CancelledDuringReadiness_DoesNotContinueDiagnosis()
    {
        using var cts = new CancellationTokenSource();
        var fakeAi = new RecordingFakeAiService();
        var service = CreateUnitDiagnosisService(
            fakeAi,
            new CancellingFakeReadiness(cts));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DiagnoseAsync(
                CreateRequest("log"), CreateConfiguration(), cts.Token));

        Assert.Empty(fakeAi.CallsWith);
    }

    private DiagnosisService CreateDiagnosisService(
        DiagnosticConfigurationStore store)
    {
        return new DiagnosisService(
            _server.CreateServiceWith(store),
            new OllamaAiReadinessService(_server.CreateConnectionService()),
            new DiagnosisPromptBuilder(() => store.Snapshot().Input),
            new SafetyGuardService());
    }

    private DiagnosisService CreateUnitDiagnosisService(
        RecordingFakeAiService ai,
        IAiReadinessService readiness)
    {
        return new DiagnosisService(
            ai,
            readiness,
            new DiagnosisPromptBuilder(new DiagnosisInputOptions()),
            new SafetyGuardService());
    }

    private static DiagnosticConfiguration CreateConfiguration(
        string baseUrl = "http://primary:11434",
        string modelName = "model-a",
        bool useJsonFormat = true,
        int maxLogLength = 1200)
    {
        return new DiagnosticConfiguration(
            new OllamaOptions
            {
                BaseUrl = baseUrl,
                ModelName = modelName,
                UseJsonFormat = useJsonFormat,
                TimeoutSeconds = 30
            },
            new DiagnosisInputOptions { MaxFaultLogCharacters = maxLogLength });
    }

    private static DiagnosticConfiguration SnapshotFor(
        string baseUrl,
        string modelName,
        bool useJsonFormat,
        int maxLogLength) =>
        CreateConfiguration(baseUrl, modelName, useJsonFormat, maxLogLength);

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

    private static string GetModelProperty(JsonDocument chatBody, string property)
    {
        return chatBody.RootElement.GetProperty(property).GetString()!;
    }

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

    private sealed class RecordingFakeAiService : IAiService
    {
        public List<OllamaOptions> CallsWith { get; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            CancellationToken cancellationToken = default) =>
            GetDiagnosticResultAsync(systemPrompt, userContext, new OllamaOptions(), cancellationToken);

        public Task<DiagnosticResult> GetDiagnosticResultAsync(
            string systemPrompt,
            string userContext,
            OllamaOptions options,
            CancellationToken cancellationToken = default)
        {
            CallsWith.Add(options);
            return Task.FromResult(CannedResult());
        }

        internal static DiagnosticResult CannedResult() =>
            new()
            {
                Summary = "测试摘要",
                RootCause = "已有证据：日志；不确定因素：无。无法确定，需要进一步测试",
                Confidence = 0.5,
                RiskLevel = DiagnosticRiskLevel.Medium,
                Evidence =
                [
                    new DiagnosticEvidence { Kind = EvidenceKind.Fact, Description = "日志事实" }
                ],
                Recommendations =
                [
                    new Recommendation
                    {
                        Action = "复测",
                        Reason = "验证",
                        RiskLevel = DiagnosticRiskLevel.Low
                    }
                ]
            };
    }

    private sealed class StaticFakeReadiness : IAiReadinessService
    {
        private readonly AiReadinessStatus _status;
        private readonly string _message;

        public StaticFakeReadiness(AiReadinessStatus status, string message = "")
        {
            _status = status;
            _message = message;
        }

        public List<OllamaOptions> CheckCalls { get; } = [];

        public Task<AiReadinessResult> CheckReadinessAsync(
            OllamaOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            CheckCalls.Add(options);
            return Task.FromResult(new AiReadinessResult(_status, _message));
        }
    }

    private sealed class CancellingFakeReadiness : IAiReadinessService
    {
        private readonly CancellationTokenSource _source;

        public CancellingFakeReadiness(CancellationTokenSource source)
        {
            _source = source;
        }

        public Task<AiReadinessResult> CheckReadinessAsync(
            OllamaOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken = _source.Token;
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose() => _server.Dispose();
}
