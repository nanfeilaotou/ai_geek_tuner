using System.Net.Http;
using System.Net;
using System.Text.Json;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Tests.Services.AI;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers.Runtime;

/// <summary>V2-M5.1B Gate R：两种 transport 的 payload / 鉴权 / 端点 / 错误映射（全部 Stub HttpMessageHandler）。</summary>
public class AiChatTransportTests : IDisposable
{
    private readonly StubOllamaServer _server = new();

    // ---------------- Ollama Native ----------------

    [Fact]
    public async Task Ollama_NativeSchema_SendsSchemaAsFormat()
    {
        var schema = """
{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}
""";
        var response = await SendOllamaAsync(
            AiStructuredOutputMode.NativeSchema,
            AiStructuredOutputRequest.WithSchema(schema));

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        var format = body.RootElement.GetProperty("format");
        Assert.Equal(JsonValueKind.Object, format.ValueKind);
        Assert.Equal("object", format.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Ollama_JsonObject_SendsFormatJson()
    {
        await SendOllamaAsync(
            AiStructuredOutputMode.NativeSchema,
            AiStructuredOutputRequest.JsonObject);

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Ollama_PromptOnly_OmitsFormat()
    {
        var response = await SendOllamaAsync(
            AiStructuredOutputMode.PromptOnly,
            AiStructuredOutputRequest.JsonObject);

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.False(body.RootElement.TryGetProperty("format", out _));
    }

    [Fact]
    public async Task Ollama_ThinkFalse_IsPreserved_AndModelFromSnapshot()
    {
        var response = await SendOllamaAsync(
            AiStructuredOutputMode.NativeSchema,
            AiStructuredOutputRequest.JsonObject,
            options: new AiChatRuntimeOptions(Think: false, Temperature: 0.2));

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("qwen3:8b", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.2, body.RootElement.GetProperty("options").GetProperty("temperature").GetDouble(), 5);
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }

    // ---------------- OpenAI Compatible ----------------

    [Fact]
    public async Task OpenAi_OpenAiJsonSchema_SendsResponseFormatJsonSchema()
    {
        var schema = """
{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]}
""";
        await SendOpenAiAsync(
            AiStructuredOutputMode.OpenAiJsonSchema,
            AiStructuredOutputRequest.WithSchema(schema));

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        var responseFormat = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        Assert.True(responseFormat.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("string", responseFormat
            .GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").GetProperty("summary").GetProperty("type").GetString());
    }

    [Fact]
    public async Task OpenAi_JsonObjectIntent_SendsResponseFormatJsonObject()
    {
        await SendOpenAiAsync(
            AiStructuredOutputMode.OpenAiJsonSchema,
            AiStructuredOutputRequest.JsonObject);

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.Equal("json_object", body.RootElement
            .GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task OpenAi_PromptOnly_OmitsResponseFormat()
    {
        await SendOpenAiAsync(
            AiStructuredOutputMode.PromptOnly,
            AiStructuredOutputRequest.JsonObject);

        using var body = JsonDocument.Parse(_server.RequestBodies[0]!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task OpenAi_NativeSchemaMode_IsExplicitlyRejected()
    {
        // Gate G：OpenAI 兼容 + NativeSchema 必须显式 ProtocolError，绝不偷偷换语义。
        var exception = await Assert.ThrowsAsync<AiRuntimeException>(() =>
            SendOpenAiAsync(
                AiStructuredOutputMode.NativeSchema,
                AiStructuredOutputRequest.JsonObject));

        Assert.Equal(AiRuntimeError.ProtocolError, exception.Error);
        Assert.Empty(_server.RequestUrls); // 请求根本不该发出去
    }

    [Fact]
    public async Task OpenAi_BearerHeader_IncludedWhenKeyExists_AbsentWhenNot()
    {
        var recorder = new HeaderRecorder();

        // 有 Key：带 Bearer。
        var transportWithKey = new OpenAiCompatibleChatTransport(recorder.CreateClient());
        await transportWithKey.SendAsync(
            CreateRequest(
                AiStructuredOutputMode.OpenAiJsonSchema,
                AiStructuredOutputRequest.JsonObject,
                kind: AiProviderKind.OpenAiCompatible,
                apiKey: "sk-visible-key"),
            CancellationToken.None);
        Assert.Equal("Bearer sk-visible-key", recorder.LastAuthorization);

        // 无 Key：绝不带 Authorization 头（LM Studio / llama.cpp 无鉴权合法）。
        recorder.LastAuthorization = null;
        var transportNoKey = new OpenAiCompatibleChatTransport(recorder.CreateClient());
        await transportNoKey.SendAsync(
            CreateRequest(
                AiStructuredOutputMode.OpenAiJsonSchema,
                AiStructuredOutputRequest.JsonObject,
                kind: AiProviderKind.OpenAiCompatible),
            CancellationToken.None);
        Assert.Null(recorder.LastAuthorization);
    }

    private sealed class HeaderRecorder : HttpMessageHandler
    {
        public string? LastAuthorization { get; set; }

        public HttpClient CreateClient()
        {
            var client = new HttpClient(this);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
{"choices":[{"message":{"role":"assistant","content":"ok"}}]}
""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
        }
    }
    [Fact]
    public async Task OpenAi_Endpoint_RootHandling_NoDoubleV1()
    {
        // LM Studio / llama.cpp（/v1 结尾）→ /v1/chat/completions，绝不 /v1/v1。
        await SendOpenAiAsync(
            AiStructuredOutputMode.OpenAiJsonSchema,
            AiStructuredOutputRequest.JsonObject,
            baseUrl: "http://127.0.0.1:1234/v1");

        Assert.EndsWith("/v1/chat/completions", _server.RequestUrls[0]);
    }

    [Fact]
    public async Task OpenAi_DeepSeekStyleRootWithoutV1_AppendsChatCompletions()
    {
        await SendOpenAiAsync(
            AiStructuredOutputMode.OpenAiJsonSchema,
            AiStructuredOutputRequest.JsonObject,
            baseUrl: "https://api.deepseek.example");

        Assert.EndsWith("/chat/completions", _server.RequestUrls[0]);
        Assert.DoesNotContain("/v1/v1", _server.RequestUrls[0]);
    }

    // ---------------- 错误映射（Gate P） ----------------

    [Fact]
    public async Task Http401_MapsToAuthenticationFailed_WithSanitizedMessage()
    {
        _server.EnqueueHttpStatus(HttpStatusCode.Unauthorized, """
{"error":{"message":" Incorrect API key provided.","type":"invalid_request_error"}}
""");
        var exception = await Assert.ThrowsAsync<AiRuntimeException>(
            () => SendOpenAiAsync(AiStructuredOutputMode.OpenAiJsonSchema, AiStructuredOutputRequest.JsonObject));

        Assert.Equal(AiRuntimeError.AuthenticationFailed, exception.Error);
        Assert.Contains("认证失败", exception.UserMessage);
        Assert.DoesNotContain("Incorrect API key", exception.UserMessage); // 响应体原文绝不回显
    }

    [Fact]
    public async Task Http404_MapsToModelRejected()
    {
        _server.EnqueueHttpStatus(HttpStatusCode.NotFound, "{}");

        var exception = await Assert.ThrowsAsync<AiRuntimeException>(
            () => SendOpenAiAsync(AiStructuredOutputMode.OpenAiJsonSchema, AiStructuredOutputRequest.JsonObject));

        Assert.Equal(AiRuntimeError.ModelRejected, exception.Error);
        Assert.Contains("模型", exception.UserMessage);
    }

    [Fact]
    public async Task Ollama_EmptyContent_MapsToInvalidResponse()
    {
        _server.EnqueueHttpStatus(HttpStatusCode.OK, """
{"message":{"role":"assistant","content":""},"done":true}
""");

        var exception = await Assert.ThrowsAsync<AiRuntimeException>(
            () => SendOllamaAsync(AiStructuredOutputMode.NativeSchema, AiStructuredOutputRequest.JsonObject));

        Assert.Equal(AiRuntimeError.InvalidResponse, exception.Error);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutError()
    {
        // 快照超时 1 秒，响应延迟 2 秒 → Timeout。
        _server.EnqueueDelayedTextResponse("late", TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<AiRuntimeException>(
            () => SendOllamaAsync(AiStructuredOutputMode.NativeSchema, AiStructuredOutputRequest.JsonObject));

        Assert.Equal(AiRuntimeError.Timeout, exception.Error);
        Assert.Equal("AI 请求超时。", exception.UserMessage);
    }

    // ---------------- helpers ----------------

    private AiChatRequest CreateRequest(
        AiStructuredOutputMode mode,
        AiStructuredOutputRequest structured,
        string baseUrl = "http://localhost:11434",
        AiProviderKind kind = AiProviderKind.OllamaNative,
        string? apiKey = null,
        AiChatRuntimeOptions? options = null) =>
        new(
            new AiRuntimeSnapshot(
                "p1", "测试 Provider", kind, baseUrl, "qwen3:8b", mode, apiKey, TimeoutSeconds: 1),
            [new AiChatMessage("system", "s"), new AiChatMessage("user", "u")],
            structured,
            options ?? new AiChatRuntimeOptions(Think: false));

    private void EnqueueOllamaOk() => _server.EnqueueTextResponse("ok");

    private void EnqueueOpenAiOk() => _server.EnqueueHttpStatus(HttpStatusCode.OK, """
{"choices":[{"message":{"role":"assistant","content":"ok"}}]}
""");

    private async Task<AiChatResponse> SendOllamaAsync(
        AiStructuredOutputMode mode,
        AiStructuredOutputRequest structured,
        AiChatRuntimeOptions? options = null)
    {
        EnqueueOllamaOk();
        var transport = new OllamaNativeChatTransport(_server.CreateClient());
        return await transport.SendAsync(
            CreateRequest(mode, structured, kind: AiProviderKind.OllamaNative, options: options));
    }

    private async Task<AiChatResponse> SendOpenAiAsync(
        AiStructuredOutputMode mode,
        AiStructuredOutputRequest structured,
        string baseUrl = "https://api.deepseek.example",
        string? apiKey = null)
    {
        EnqueueOpenAiOk();
        var transport = new OpenAiCompatibleChatTransport(_server.CreateClient());
        return await transport.SendAsync(
            CreateRequest(
                mode, structured,
                baseUrl: baseUrl,
                kind: AiProviderKind.OpenAiCompatible,
                apiKey: apiKey,
                options: new AiChatRuntimeOptions(Think: false, MaxOutputTokens: 512)));
    }

    public void Dispose() => _server.Dispose();
}
