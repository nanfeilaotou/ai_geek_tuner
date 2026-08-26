using System.Net;
using System.Net.Http;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.AI;

/// <summary>
/// 连接检测三态 + 模型列表解析契约；全部经由确定性 HttpMessageHandler 替身。
/// </summary>
public class OllamaConnectionServiceTests
{
    private const string TagsJson =
        """
{"models":[
  {"name":"qwen3:8b"},
  {"name":"llama3.1:8b"},
  {"name":"nomic-embed-text"}
]}
""";

    [Fact]
    public async Task Check_WhenHandlerThrowsHttpRequestException_ReturnsServiceUnavailable()
    {
        var server = new StubOllamaServer();
        server.EnqueueThrow(new HttpRequestException("connection refused"));
        var service = server.CreateConnectionService();

        var result = await service.CheckReadinessAsync(
            "http://localhost:11434", "qwen3:8b");

        Assert.Equal(OllamaReadinessStatus.ServiceUnavailable, result.Status);
        Assert.Contains("未启动或无法连接", result.Message);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task Check_WhenModelInstalled_ReturnsReadyWithModelList()
    {
        var server = new StubOllamaServer();
        server.EnqueueTags(TagsJson);
        var service = server.CreateConnectionService();

        var result = await service.CheckReadinessAsync(
            "http://localhost:11434", "qwen3:8b");

        Assert.Equal(OllamaReadinessStatus.Ready, result.Status);
        Assert.Contains("已就绪", result.Message);
        Assert.Contains("qwen3:8b", result.Message);
        Assert.Equal(3, result.Models.Count);
    }

    [Fact]
    public async Task Check_WhenModelMissing_ReturnsModelMissing()
    {
        var server = new StubOllamaServer();
        server.EnqueueTags(TagsJson);
        var service = server.CreateConnectionService();

        var result = await service.CheckReadinessAsync(
            "http://localhost:11434", "gemma2:9b");

        Assert.Equal(OllamaReadinessStatus.ModelMissing, result.Status);
        Assert.Contains("未找到模型 gemma2:9b", result.Message);
        // 模型列表仍然返回，便于用户直接选择已安装项。
        Assert.Equal(3, result.Models.Count);
    }

    [Fact]
    public async Task GetModels_ParsesAllNamesFromTagsResponse()
    {
        var server = new StubOllamaServer();
        server.EnqueueTags(TagsJson);
        var service = server.CreateConnectionService();

        var models = await service.GetModelsAsync("http://localhost:11434");

        Assert.Equal(
            new[] { "qwen3:8b", "llama3.1:8b", "nomic-embed-text" },
            models.ToArray());
    }

    [Fact]
    public async Task GetModels_MalformedJson_ThrowsTypedConnectionError()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse("this is not json at all");
        var service = server.CreateConnectionService();

        var exception = await Assert.ThrowsAsync<OllamaConnectionException>(
            () => service.GetModelsAsync("http://localhost:11434"));

        Assert.Contains("格式无法解析", exception.Message);
    }
}
