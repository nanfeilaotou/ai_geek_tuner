using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.Safety;

namespace AIGeekTuner.Tests.Services.Diagnosis;

public sealed class DiagnosticGroundingTests
{
    [Fact]
    public void FactQuoteMustBeAnOrdinalSubstringOfTheSelectedSource()
    {
        var context = new AIGeekTuner.Services.Diagnosis.DiagnosisPromptBuilder().BuildContext(
            CreateRequest("我电脑卡了", "电脑出现卡顿"),
            new DiagnosisInputOptions());
        var validator = new DiagnosticGroundingValidator();

        validator.Validate(CreateResult(
            new DiagnosticEvidence
            {
                Kind = EvidenceKind.Fact,
                Description = "用户反馈",
                SourceId = DiagnosticEvidenceSourceIds.FaultLog,
                SourceQuote = "我电脑卡了"
            }), context);

        Assert.Throws<DiagnosticGroundingValidationException>(() =>
        {
            validator.Validate(CreateResult(
                new DiagnosticEvidence
                {
                    Kind = EvidenceKind.Fact,
                    Description = "用户反馈电箱坏了",
                    SourceId = DiagnosticEvidenceSourceIds.FaultLog,
                    SourceQuote = "我电箱烂了"
                }), context);
        });
    }

    [Fact]
    public void InvalidSourceIdEmptyQuoteAndKnowledgeReferenceAreRejected()
    {
        var context = new AIGeekTuner.Services.Diagnosis.DiagnosisPromptBuilder().BuildContext(
            CreateRequest("故障日志", "用户描述"),
            new DiagnosisInputOptions());
        var validator = new DiagnosticGroundingValidator();

        foreach (var evidence in new[]
        {
            new DiagnosticEvidence
            {
                Kind = EvidenceKind.Fact,
                Description = "事实",
                SourceId = "source:previous-request",
                SourceQuote = "故障日志"
            },
            new DiagnosticEvidence
            {
                Kind = EvidenceKind.Fact,
                Description = "事实",
                SourceId = DiagnosticEvidenceSourceIds.FaultLog,
                SourceQuote = ""
            },
            new DiagnosticEvidence
            {
                Kind = EvidenceKind.Fact,
                Description = "知识库候选",
                SourceId = "knowledge:memory",
                SourceQuote = "故障日志"
            }
        })
        {
            Assert.Throws<DiagnosticGroundingValidationException>(() =>
            {
                validator.Validate(CreateResult(evidence), context);
            });
        }
    }

    [Fact]
    public void PromptKeepsUserDescriptionAndFaultLogAsIndependentSources()
    {
        var context = new AIGeekTuner.Services.Diagnosis.DiagnosisPromptBuilder().BuildContext(
            CreateRequest("日志 B", "问题 A"),
            new DiagnosisInputOptions());

        Assert.Equal("日志 B", context.FindSource(DiagnosticEvidenceSourceIds.FaultLog)!.Content);
        Assert.Equal("问题 A", context.FindSource(DiagnosticEvidenceSourceIds.UserDescription)!.Content);
        Assert.Contains("[source:fault-log]", context.UserMessage);
        Assert.Contains("[source:user-description]", context.UserMessage);
        Assert.DoesNotContain("[用户描述]", context.FindSource(DiagnosticEvidenceSourceIds.FaultLog)!.Content);
    }

    [Fact]
    public async Task GroundingFailureRepairsExactlyOnceAndUsesSameRuntimeSnapshot()
    {
        var runtime = new ScriptedRuntime(
            "{\"summary\":\"摘要\",\"rootCause\":\"无法确定，需要进一步测试\",\"confidence\":0.2,\"riskLevel\":\"Low\",\"evidence\":[{\"kind\":\"Fact\",\"description\":\"错误引述\",\"sourceId\":\"source:fault-log\",\"sourceQuote\":\"我电箱烂了\"}],\"recommendations\":[]}",
            "{\"summary\":\"摘要\",\"rootCause\":\"无法确定，需要进一步测试\",\"confidence\":0.2,\"riskLevel\":\"Low\",\"evidence\":[{\"kind\":\"Fact\",\"description\":\"真实引述\",\"sourceId\":\"source:fault-log\",\"sourceQuote\":\"我电脑卡了\"}],\"recommendations\":[]}");
        var service = new DiagnosisService(
            runtime,
            new DiagnosisPromptBuilder(),
            new SafetyGuardService());

        var outcome = await service.DiagnoseAsync(
            CreateRequest("我电脑卡了", null),
            new DiagnosticConfiguration(
                new OllamaOptions { TimeoutSeconds = 30 },
                new DiagnosisInputOptions()));

        Assert.Equal(2, runtime.Messages.Count);
        Assert.Equal("我电脑卡了", outcome.AiResult.Evidence[0].SourceQuote);
        Assert.Contains("grounding", runtime.Messages[1][^1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Same(runtime.Snapshot, runtime.Snapshots[0]);
        Assert.Same(runtime.Snapshot, runtime.Snapshots[1]);
    }

    [Fact]
    public async Task GroundingFailureTwiceFailsClosed()
    {
        var invalid = "{\"summary\":\"摘要\",\"rootCause\":\"无法确定，需要进一步测试\",\"confidence\":0.2,\"riskLevel\":\"Low\",\"evidence\":[{\"kind\":\"Fact\",\"description\":\"错误引述\",\"sourceId\":\"source:fault-log\",\"sourceQuote\":\"不存在\"}],\"recommendations\":[]}";
        var runtime = new ScriptedRuntime(invalid, invalid);
        var service = new DiagnosisService(runtime, new DiagnosisPromptBuilder(), new SafetyGuardService());

        var exception = await Assert.ThrowsAsync<DiagnosisException>(() => service.DiagnoseAsync(
            CreateRequest("我电脑卡了", null),
            new DiagnosticConfiguration(
                new OllamaOptions { TimeoutSeconds = 30 },
                new DiagnosisInputOptions())));

        Assert.Equal(DiagnosisError.AiResponseInvalid, exception.Error);
        Assert.Equal(2, runtime.Messages.Count);
    }

    private static DiagnosticRequest CreateRequest(string faultLog, string? userDescription) => new()
    {
        RequestId = Guid.NewGuid(),
        Hardware = new HardwareInfo { CpuName = "Test CPU" },
        FaultLog = FaultLog.FromPastedText(faultLog),
        UserDescription = userDescription
    };

    private static DiagnosticResult CreateResult(DiagnosticEvidence evidence) => new()
    {
        Summary = "摘要",
        RootCause = "无法确定，需要进一步测试",
        Confidence = 0.2,
        RiskLevel = DiagnosticRiskLevel.Low,
        Evidence = [evidence],
        Recommendations = []
    };

    private sealed class ScriptedRuntime(params string[] responses) : IAiChatRuntime
    {
        private readonly Queue<string> _responses = new(responses);

        public AiRuntimeSnapshot Snapshot { get; } = new(
            "test", "Test", AiProviderKind.OpenAiCompatible,
            "https://example.invalid/v1", "model", AiStructuredOutputMode.PromptOnly,
            null, 30);

        public List<IReadOnlyList<AiChatMessage>> Messages { get; } = [];
        public List<AiRuntimeSnapshot> Snapshots { get; } = [];

        public Task<AiRuntimeSnapshot> CaptureSnapshotAsync(int timeoutSeconds, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<AiReadinessResult> CheckReadinessAsync(AiRuntimeSnapshot runtime, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiReadinessResult(AiReadinessStatus.Ready, "ready"));

        public Task<string> SendChatAsync(
            AiRuntimeSnapshot runtime,
            IReadOnlyList<AiChatMessage> messages,
            AiStructuredOutputRequest structuredOutput,
            AiChatRuntimeOptions options,
            CancellationToken cancellationToken = default)
        {
            Snapshots.Add(runtime);
            Messages.Add(messages.ToArray());
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
