using System.Net;
using System.Text.Json;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Runtime;

namespace AIGeekTuner.Tests.Services.AI.Providers.Runtime;

public sealed class ProviderEncodingRegressionTests
{
    [Fact]
    public async Task OllamaAndOpenAiPreserveUnicodeUserContentAfterHttpJsonRoundtrip()
    {
        const string text = "我电脑卡了，温度 87℃，显卡 RTX 4080。";
        var messages = new[]
        {
            new AiChatMessage("system", "system"),
            new AiChatMessage("user", text)
        };

        using var ollama = new StubOllamaServer();
        ollama.EnqueueTextResponse("ok");
        var ollamaTransport = new OllamaNativeChatTransport(ollama.CreateClient());
        await ollamaTransport.SendAsync(new AiChatRequest(
            Snapshot(AiProviderKind.OllamaNative, "http://ollama.local:11434", AiStructuredOutputMode.JsonObject),
            messages,
            AiStructuredOutputRequest.JsonObject,
            new AiChatRuntimeOptions()));

        using var openAi = new StubOllamaServer();
        openAi.EnqueueHttpStatus(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        var openAiTransport = new OpenAiCompatibleChatTransport(openAi.CreateClient());
        await openAiTransport.SendAsync(new AiChatRequest(
            Snapshot(AiProviderKind.OpenAiCompatible, "http://lmstudio.local:1234/v1", AiStructuredOutputMode.JsonObject),
            messages,
            AiStructuredOutputRequest.JsonObject,
            new AiChatRuntimeOptions()));

        var ollamaContent = ReadUserContent(ollama.RequestBodies.Single()!);
        var openAiContent = ReadUserContent(openAi.RequestBodies.Single()!);
        Assert.Equal(text, ollamaContent);
        Assert.Equal(text, openAiContent);
        Assert.Equal(ollamaContent, openAiContent);
    }

    private static string ReadUserContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages")[1]
            .GetProperty("content").GetString()!;
    }

    private static AiRuntimeSnapshot Snapshot(
        AiProviderKind kind,
        string baseUrl,
        AiStructuredOutputMode mode) => new(
            "test", "Test", kind, baseUrl, "model", mode, null, 30);
}
