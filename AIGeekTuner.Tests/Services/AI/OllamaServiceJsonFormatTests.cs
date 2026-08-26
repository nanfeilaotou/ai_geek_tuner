using System.Text.Json;
using AIGeekTuner.Configuration;

namespace AIGeekTuner.Tests.Services.AI;

/// <summary>
/// 验证 UseJsonFormat 开关对“最终序列化请求体”的影响：
/// 开启时包含官方 JSON mode 字段 "format":"json"；关闭时整个字段省略。
/// </summary>
public class OllamaServiceJsonFormatTests
{
    [Fact]
    public async Task JsonModeEnabled_RequestContainsFormatJson()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var service = server.CreateService(new OllamaOptions { UseJsonFormat = true });

        await service.GetDiagnosticResultAsync("system", "user");

        Assert.Single(server.RequestBodies);
        using var body = JsonDocument.Parse(server.RequestBodies[0]!);
        Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("qwen3:8b", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task JsonModeDisabled_FormatFieldIsOmittedFromRequestBody()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var service = server.CreateService(new OllamaOptions { UseJsonFormat = false });

        await service.GetDiagnosticResultAsync("system", "user");

        Assert.Single(server.RequestBodies);
        using var body = JsonDocument.Parse(server.RequestBodies[0]!);
        Assert.False(body.RootElement.TryGetProperty("format", out _));
    }
}
