using System.Net;
using System.Net.Http;
using AIGeekTuner.Services.AI.Providers.Transport;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers;

/// <summary>Gate E/F/G/I/J/O：OpenAI 兼容传输（models、鉴权、chat 探测、超时、取消）。</summary>
public sealed class OpenAiCompatibleClientTests
{
    private const string LmStudioBase = "http://127.0.0.1:1234/v1";

    private static OpenAiCompatibleClient CreateClient(StubAiHttpHandler handler)
    {
        return new OpenAiCompatibleClient(new HttpClient(handler));
    }

    [Fact]
    public async Task ListModels_NormalResponse_ParsedDistinctSorted()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("""
            { "data": [ { "id": "qwen2.5-7b-instruct" }, { "id": "llama-3.1-8b" }, { "id": "qwen2.5-7b-instruct" } ] }
            """);
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(LmStudioBase, null);

        Assert.Equal(AiModelDiscoveryStatus.ModelsDiscovered, result.Status);
        Assert.Equal(new[] { "llama-3.1-8b", "qwen2.5-7b-instruct" }, result.Models);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:1234/v1/models", request.Url);
    }

    [Fact]
    public async Task ListModels_WithApiKey_SendsBearerHeader_WithoutKey_SendsNoAuthHeader()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("""{ "data": [ { "id": "m1" } ] }""");
        handler.EnqueueSuccessJson("""{ "data": [ { "id": "m1" } ] }""");
        var client = CreateClient(handler);

        await client.ListModelsAsync(LmStudioBase, "sk-live-abc");
        await client.ListModelsAsync(LmStudioBase, null);

        Assert.Equal("Bearer sk-live-abc", handler.Requests[0].Authorization);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task ListModels_MalformedJson_ModelsUnavailable_NotConnectionFailure()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("{ broken");
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(LmStudioBase, null);

        Assert.Equal(AiModelDiscoveryStatus.ModelsUnavailable, result.Status);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task ListModels_Unauthorized_AuthenticationFailed()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{ "error": { "type": "invalid_request_error" } }""");
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(LmStudioBase, "bad-key");

        Assert.Equal(AiModelDiscoveryStatus.AuthenticationFailed, result.Status);
    }

    [Fact]
    public async Task ListModels_ModelsEndpoint404_ModelsUnavailable_ProviderStillUsable()
    {
        // Open WebUI 经验：/models 不存在 ≠ Provider 不可用。
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "no such route");
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(LmStudioBase, null);

        Assert.Equal(AiModelDiscoveryStatus.ModelsUnavailable, result.Status);
        Assert.Contains("手动", result.Message);
    }

    [Fact]
    public async Task ListModels_ServiceDown_ConnectionUnavailable()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(LmStudioBase, null);

        Assert.Equal(AiModelDiscoveryStatus.ConnectionUnavailable, result.Status);
    }

    [Fact]
    public async Task ProbeChat_Success_ReturnsConnected()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("""{ "choices": [ { "message": { "role": "assistant", "content": "pong" } } ] }""");
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(LmStudioBase, null, " llama-3.1-8b ");

        Assert.Equal(AiConnectionTestStatus.Connected, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", request.Url);
        Assert.Contains("\"stream\":false", request.Body);
        Assert.Contains("ping", request.Body);
    }

    [Fact]
    public async Task ProbeChat_Unauthorized_AuthenticationFailed()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{ "error": { "type": "invalid_api_key" } }""");
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(LmStudioBase, "bad", "m1");

        Assert.Equal(AiConnectionTestStatus.AuthenticationFailed, result.Status);
    }

    [Fact]
    public async Task ProbeChat_ModelNotFound_ModelRejected()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{ "error": { "type": "model_not_found" } }""");
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(LmStudioBase, null, "missing-model");

        Assert.Equal(AiConnectionTestStatus.ModelRejected, result.Status);
    }

    [Fact]
    public async Task ProbeChat_RetriesWithMaxCompletionTokens_WhenEndpointRequiresIt()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{ "error": { "type": "use max_completion_tokens instead of max_tokens" } }""");
        handler.EnqueueSuccessJson("""{ "choices": [ { "message": { "role": "assistant" } } ] }""");
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(LmStudioBase, null, "o1-mini");

        Assert.Equal(AiConnectionTestStatus.Connected, result.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"max_tokens\":16", handler.Requests[0].Body);
        Assert.DoesNotContain("\"max_tokens\"", handler.Requests[1].Body);
        Assert.Contains("\"max_completion_tokens\":16", handler.Requests[1].Body);
    }

    [Fact]
    public async Task ProbeChat_Timeout_ConnectionUnavailable()
    {
        var handler = new StubAiHttpHandler();
        // stub 不观察取消令牌，这里直接以 TaskCanceledException 模拟“链接 CTS 超时”
        //（用户令牌未取消 → 走超时分支而非取消传播分支）。
        handler.Enqueue(_ => throw new TaskCanceledException("simulated timeout"));
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(LmStudioBase, null, "m1");

        Assert.Equal(AiConnectionTestStatus.ConnectionUnavailable, result.Status);
    }

    [Fact]
    public async Task ProbeChat_UserCancellation_Propagates()
    {
        var handler = new StubAiHttpHandler();
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ProbeChatAsync(LmStudioBase, null, "m1", cts.Token));
    }

    [Fact]
    public async Task ProbeChat_MissingModel_MissingModelStatus()
    {
        var client = CreateClient(new StubAiHttpHandler());

        var result = await client.ProbeChatAsync(LmStudioBase, null, "   ");

        Assert.Equal(AiConnectionTestStatus.MissingModel, result.Status);
    }

    [Fact]
    public async Task ProbeStructuredOutput_RejectionDoesNotInvalidateProvider()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{ "error": { "type": "unsupported" } }""");
        var client = CreateClient(handler);

        var result = await client.ProbeStructuredOutputAsync(LmStudioBase, null, "m1");

        Assert.False(result.Supported);
        Assert.Contains("不影响 Provider 配置", result.Message);
    }

    [Fact]
    public async Task ProbeStructuredOutput_Supported_WhenEndpointAcceptsSchema()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("""{ "choices": [ { "message": { "role": "assistant" } } ] }""");
        var client = CreateClient(handler);

        var result = await client.ProbeStructuredOutputAsync(LmStudioBase, null, "m1");

        Assert.True(result.Supported);
        Assert.Contains("json_schema", handler.Requests[0].Body);
    }
}

/// <summary>Gate K/O：Ollama 原生 adapter（/api/tags、/api/chat 探测）。</summary>
public sealed class OllamaNativeClientTests
{
    private const string OllamaBase = "http://localhost:11434";

    private static OllamaNativeClient CreateClient(StubAiHttpHandler handler)
    {
        return new OllamaNativeClient(new HttpClient(handler));
    }

    [Fact]
    public async Task ListModels_TagsResponse_ParsedDistinctSorted()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("""
            { "models": [ { "name": "qwen3:8b" }, { "name": "llama3:latest" }, { "name": "qwen3:8b" } ] }
            """);
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(OllamaBase);

        Assert.Equal(AiModelDiscoveryStatus.ModelsDiscovered, result.Status);
        Assert.Equal(new[] { "llama3:latest", "qwen3:8b" }, result.Models);
        Assert.Equal("http://localhost:11434/api/tags", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task ListModels_ServiceDown_ConnectionUnavailable()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(_ => throw new HttpRequestException("refused"));
        var client = CreateClient(handler);

        var result = await client.ListModelsAsync(OllamaBase);

        Assert.Equal(AiModelDiscoveryStatus.ConnectionUnavailable, result.Status);
    }

    [Fact]
    public async Task ProbeChat_Success_ReturnsConnected()
    {
        var handler = new StubAiHttpHandler();
        handler.EnqueueSuccessJson("""{ "model": "qwen3:8b", "done": true }""");
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(OllamaBase, "qwen3:8b");

        Assert.Equal(AiConnectionTestStatus.Connected, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:11434/api/chat", request.Url);
        Assert.Contains("\"num_predict\":16", request.Body);
    }

    [Fact]
    public async Task ProbeChat_ModelMissing_ModelRejected()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{ "error": "model not found" }""");
        var client = CreateClient(handler);

        var result = await client.ProbeChatAsync(OllamaBase, "ghost");

        Assert.Equal(AiConnectionTestStatus.ModelRejected, result.Status);
    }

    [Fact]
    public async Task ProbeStructuredOutput_RejectionIsCapabilityOnly()
    {
        var handler = new StubAiHttpHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{ "error": "format unsupported" }""");
        var client = CreateClient(handler);

        var result = await client.ProbeStructuredOutputAsync(OllamaBase, "qwen3:8b");

        Assert.False(result.Supported);
        Assert.Contains("不影响 Provider 配置", result.Message);
    }
}
