using System.Net;
using System.Text.Json;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Diagnosis;

namespace AIGeekTuner.Tests.Services.AI;

/// <summary>
/// Repair 行为契约：
/// A/F 合法输出只发一次请求；B 首次结构无效恰好补发一次修复请求；
/// C 两次无效绝不出现第三次；D HTTP 错误、E 取消绝不触发修复。
/// </summary>
public class OllamaServiceRepairTests
{
    private const string InvalidResponse = "not a json response at all";

    [Fact]
    public async Task CaseA_FirstResponseValid_SendsSingleChatRequest()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var service = server.CreateService();

        var result = await service.GetDiagnosticResultAsync("system", "user");

        Assert.Equal(0.55, result.Confidence, precision: 9);
        Assert.Single(server.RequestUrls);
        Assert.EndsWith("/api/chat", server.RequestUrls[0]);
    }

    [Fact]
    public async Task CaseB_FirstInvalidThenValid_SendsExactlyOneRepairRequest()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(InvalidResponse);
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var service = server.CreateService();

        var result = await service.GetDiagnosticResultAsync("system", "user");

        // 最终成功，且总请求数为 2（原始 + 一次修复）
        Assert.Single(result.Recommendations);
        Assert.Equal(2, server.RequestUrls.Count);

        // 修复请求保留原上下文，并携带首次无效输出与修复指令
        using var body = JsonDocument.Parse(server.RequestBodies[1]!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal(InvalidResponse, messages[2].GetProperty("content").GetString());
        var repairInstruction = messages[3].GetProperty("content").GetString();
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Contains("只输出一个合法的 JSON 对象", repairInstruction);
        Assert.Contains("禁止编造温度、电压、功耗", repairInstruction);
    }

    [Fact]
    public async Task CaseC_BothResponsesInvalid_FailsWithoutThirdRequest()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(InvalidResponse);
        server.EnqueueTextResponse("still { broken");
        var service = server.CreateService();

        var exception = await Assert.ThrowsAsync<DiagnosticResultParsingException>(
            () => service.GetDiagnosticResultAsync("system", "user"));

        Assert.Contains("两次输出均无法解析", exception.Message);
        Assert.Equal(2, server.RequestUrls.Count);
    }

    [Fact]
    public async Task CaseD_HttpError_DoesNotRetry()
    {
        var server = new StubOllamaServer();
        server.EnqueueHttpStatus(HttpStatusCode.InternalServerError, "{}");
        var service = server.CreateService();

        await Assert.ThrowsAsync<OllamaServiceException>(
            () => service.GetDiagnosticResultAsync("system", "user"));

        Assert.Single(server.RequestUrls);
    }

    [Fact]
    public async Task CaseE_CancelledDuringFirstRequest_DoesNotSendRepair()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new StubOllamaServer();
        server.OnFirstRequest(cancellation.Cancel);
        server.EnqueueTextResponse(InvalidResponse);
        var service = server.CreateService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetDiagnosticResultAsync("system", "user", cancellation.Token));

        // 第一次请求已发出；修复请求因取消从未到达服务器。
        Assert.Single(server.RequestUrls);
    }

    [Fact]
    public async Task CaseF_ValidJsonInsideCodeFence_SendsSingleChatRequest()
    {
        var fenced = "```json\n" + StubOllamaServer.ValidDiagnosticJson + "\n```";
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(fenced);
        var service = server.CreateService();

        var result = await service.GetDiagnosticResultAsync("system", "user");

        Assert.Single(result.Evidence);
        Assert.Single(server.RequestUrls);
    }
}
