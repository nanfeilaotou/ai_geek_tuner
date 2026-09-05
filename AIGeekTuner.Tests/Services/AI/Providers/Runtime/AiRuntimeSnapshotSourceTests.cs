using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.AI.Providers.Transport;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers.Runtime;

/// <summary>V2-M5.1B Gate Q：Active Provider 解析 / fallback / 运行时快照捕获。</summary>
public class AiRuntimeSnapshotSourceTests
{
    private const string OllamaId = "ollama";
    private const string LmStudioId = "lmstudio";
    private const string LlamaCppId = "llama-cpp";
    private const string DeepSeekId = "deepseek";

    [Fact]
    public void NoActiveId_FallsBackToMigratedOllamaProfile()
    {
        // Gate C：迁移出的 ollama profile（id == "ollama"）优先作为默认 active。
        var configuration = new AiProviderConfiguration
        {
            Profiles =
            [
                Profile(OllamaId, "Ollama（从旧设置迁移）", AiProviderKind.OllamaNative,
                    "http://localhost:11434", "qwen3:8b", AiStructuredOutputMode.NativeSchema),
                Profile(LmStudioId, "LM Studio", AiProviderKind.OpenAiCompatible,
                    "http://127.0.0.1:1234/v1", "qwen2.5-7b-instruct", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        };

        var resolved = AiActiveProviderResolver.Resolve(configuration);

        Assert.NotNull(resolved);
        Assert.Equal(OllamaId, resolved.Id);
    }

    [Fact]
    public void ValidActiveId_IsUsed()
    {
        var configuration = new AiProviderConfiguration
        {
            ActiveProviderId = LmStudioId,
            Profiles =
            [
                Profile(OllamaId, "Ollama", AiProviderKind.OllamaNative,
                    "http://localhost:11434", "qwen3:8b", AiStructuredOutputMode.NativeSchema),
                Profile(LmStudioId, "LM Studio", AiProviderKind.OpenAiCompatible,
                    "http://127.0.0.1:1234/v1", "qwen2.5-7b-instruct", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        };

        var resolved = AiActiveProviderResolver.Resolve(configuration);

        Assert.NotNull(resolved);
        Assert.Equal(LmStudioId, resolved.Id);
    }

    [Fact]
    public void InvalidActiveId_FallsBackToOllamaThenFirstEnabled()
    {
        var configuration = new AiProviderConfiguration
        {
            ActiveProviderId = "ghost",
            Profiles =
            [
                Profile(OllamaId, "Ollama", AiProviderKind.OllamaNative,
                    "http://localhost:11434", "qwen3:8b", AiStructuredOutputMode.NativeSchema)
            ]
        };

        var resolved = AiActiveProviderResolver.Resolve(configuration);
        Assert.Equal(OllamaId, resolved?.Id);

        // 没有 ollama profile 时：第一个合法 Enabled profile。
        var configuration2 = new AiProviderConfiguration
        {
            ActiveProviderId = "ghost",
            Profiles =
            [
                Profile(DeepSeekId, "DeepSeek", AiProviderKind.OpenAiCompatible,
                    "https://api.deepseek.com", "deepseek-chat", AiStructuredOutputMode.OpenAiJsonSchema),
                Profile(LmStudioId, "LM Studio", AiProviderKind.OpenAiCompatible,
                    "http://127.0.0.1:1234/v1", "qwen2.5-7b-instruct", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        };
        Assert.Equal(DeepSeekId, AiActiveProviderResolver.Resolve(configuration2)?.Id);
    }

    [Fact]
    public void DisabledActiveProfile_IsSkipped()
    {
        var disabled = Profile(LmStudioId, "LM Studio", AiProviderKind.OpenAiCompatible,
            "http://127.0.0.1:1234/v1", "m", AiStructuredOutputMode.OpenAiJsonSchema, enabled: false);
        var enabled = Profile(OllamaId, "Ollama", AiProviderKind.OllamaNative,
            "http://localhost:11434", "qwen3:8b", AiStructuredOutputMode.NativeSchema);
        var configuration = new AiProviderConfiguration
        {
            ActiveProviderId = LmStudioId,
            Profiles = [disabled, enabled]
        };

        var resolved = AiActiveProviderResolver.Resolve(configuration);

        Assert.Equal(OllamaId, resolved?.Id);
    }

    [Fact]
    public void ActiveProfileWithoutDefaultModel_IsNotUsable()
    {
        var noModel = Profile(DeepSeekId, "DeepSeek", AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com", "deepseek-chat", AiStructuredOutputMode.OpenAiJsonSchema);
        noModel = new AiProviderProfile
        {
            Id = noModel.Id,
            DisplayName = noModel.DisplayName,
            Kind = noModel.Kind,
            BaseUrl = noModel.BaseUrl,
            Models = Array.Empty<AiProviderModel>(),
            DefaultModelId = null,
            StructuredOutputMode = noModel.StructuredOutputMode,
            Enabled = true
        };
        var usable = Profile(OllamaId, "Ollama", AiProviderKind.OllamaNative,
            "http://localhost:11434", "qwen3:8b", AiStructuredOutputMode.NativeSchema);
        var configuration = new AiProviderConfiguration
        {
            ActiveProviderId = DeepSeekId,
            Profiles = [noModel, usable]
        };

        var resolved = AiActiveProviderResolver.Resolve(configuration);

        Assert.Equal(OllamaId, resolved?.Id);
    }

    [Fact]
    public void EmptyConfiguration_ResolvesToNull_NoFakeConfigCreated()
    {
        Assert.Null(AiActiveProviderResolver.Resolve(AiProviderConfiguration.Empty));
    }

    [Fact]
    public async Task Capture_DeepSeekSnapshot_WithCredential()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = DeepSeekId,
            Profiles =
            [
                Profile(DeepSeekId, "DeepSeek V4 Flash", AiProviderKind.OpenAiCompatible,
                    "https://api.deepseek.com", "deepseek-chat", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        });
        var credentials = new FakeAiCredentialStore();
        await credentials.SaveAsync(DeepSeekId, "sk-test-123");
        var source = new AiRuntimeSnapshotSource(store, credentials);

        var snapshot = await source.TryCaptureAsync(timeoutSeconds: 300);

        Assert.NotNull(snapshot);
        Assert.Equal(DeepSeekId, snapshot.ProviderId);
        Assert.Equal("DeepSeek V4 Flash", snapshot.ProviderDisplayName);
        Assert.Equal(AiProviderKind.OpenAiCompatible, snapshot.ProviderKind);
        Assert.Equal("https://api.deepseek.com", snapshot.BaseUrl);
        Assert.Equal("deepseek-chat", snapshot.ModelId);
        Assert.Equal(AiStructuredOutputMode.OpenAiJsonSchema, snapshot.StructuredOutputMode);
        Assert.Equal("sk-test-123", snapshot.ApiKey);
        Assert.Equal(300, snapshot.TimeoutSeconds);
        Assert.Equal([DeepSeekId], credentials.LoadCalls);
    }
    [Fact]
    public async Task Capture_LmStudioSnapshot_WithoutKey_IsValid()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = LmStudioId,
            Profiles =
            [
                Profile(LmStudioId, "LM Studio（本机）", AiProviderKind.OpenAiCompatible,
                    "http://127.0.0.1:1234/v1", "qwen2.5-7b-instruct", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        });
        var source = new AiRuntimeSnapshotSource(store, new FakeAiCredentialStore());

        var snapshot = await source.TryCaptureAsync(60);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.ApiKey); // 无凭据的本地服务合法（LM Studio 可无鉴权）
        Assert.Equal("qwen2.5-7b-instruct", snapshot.ModelId);
    }

    [Fact]
    public async Task Capture_LlamaCppSnapshot()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = LlamaCppId,
            Profiles =
            [
                Profile(LlamaCppId, "llama.cpp", AiProviderKind.OpenAiCompatible,
                    "http://127.0.0.1:8080/v1", "qwen2.5-7b-q4", AiStructuredOutputMode.JsonObject)
            ]
        });

        var snapshot = await new AiRuntimeSnapshotSource(store, new FakeAiCredentialStore())
            .TryCaptureAsync(120);

        Assert.NotNull(snapshot);
        Assert.Equal(LlamaCppId, snapshot.ProviderId);
        Assert.Equal("http://127.0.0.1:8080/v1", snapshot.BaseUrl);
    }

    [Fact]
    public async Task Capture_OllamaSnapshot_NeverReadsCredentials()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = OllamaId,
            Profiles =
            [
                Profile(OllamaId, "Ollama", AiProviderKind.OllamaNative,
                    "http://localhost:11434", "qwen3:8b", AiStructuredOutputMode.NativeSchema)
            ]
        });
        var credentials = new FakeAiCredentialStore();
        await credentials.SaveAsync(OllamaId, "should-not-be-read");

        var snapshot = await new AiRuntimeSnapshotSource(store, credentials).TryCaptureAsync(30);

        Assert.NotNull(snapshot);
        Assert.Equal(AiProviderKind.OllamaNative, snapshot.ProviderKind);
        Assert.Null(snapshot.ApiKey);
        Assert.Empty(credentials.LoadCalls); // Ollama Native 没有鉴权概念，绝不读取凭据
    }

    [Fact]
    public async Task Capture_NotConfigured_ReturnsNull()
    {
        var source = new AiRuntimeSnapshotSource(
            new ScriptedProviderStore(AiProviderConfiguration.Empty),
            new FakeAiCredentialStore());

        Assert.Null(await source.TryCaptureAsync(30));
    }

    [Fact]
    public async Task Snapshot_IsImmutableAfterProviderOrModelOrTimeoutChanges()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = DeepSeekId,
            Profiles =
            [
                Profile(DeepSeekId, "DeepSeek V4 Flash", AiProviderKind.OpenAiCompatible,
                    "https://api.deepseek.com", "deepseek-chat", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        });
        var source = new AiRuntimeSnapshotSource(store, new FakeAiCredentialStore());
        var snapshot = await source.TryCaptureAsync(300);

        // 请求进行中：切换 Active / 改默认模型 / 改全局超时——快照原样不动。
        store.Replace(new AiProviderConfiguration
        {
            ActiveProviderId = LmStudioId,
            Profiles =
            [
                Profile(DeepSeekId, "DeepSeek V4 Flash", AiProviderKind.OpenAiCompatible,
                    "https://api.deepseek.com", "deepseek-reasoner", AiStructuredOutputMode.OpenAiJsonSchema),
                Profile(LmStudioId, "LM Studio", AiProviderKind.OpenAiCompatible,
                    "http://127.0.0.1:1234/v1", "m2", AiStructuredOutputMode.OpenAiJsonSchema)
            ]
        });

        Assert.True(snapshot is not null, "快照捕获不应返回 null。");
        Assert.Equal(DeepSeekId, snapshot!.ProviderId);
        Assert.Equal("deepseek-chat", snapshot.ModelId);
        Assert.Equal(300, snapshot.TimeoutSeconds);
    }

    [Fact]
    public void SnapshotToString_DoesNotContainApiKey()
    {
        var snapshot = new AiRuntimeSnapshot(
            DeepSeekId, "DeepSeek", AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com", "deepseek-chat",
            AiStructuredOutputMode.OpenAiJsonSchema,
            ApiKey: "sk-super-secret-value",
            TimeoutSeconds: 60);

        Assert.DoesNotContain("sk-super-secret-value", snapshot.ToString());
    }

    private static AiProviderProfile Profile(
        string id,
        string displayName,
        AiProviderKind kind,
        string baseUrl,
        string modelId,
        AiStructuredOutputMode mode,
        bool enabled = true) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Kind = kind,
            BaseUrl = baseUrl,
            Models = [new AiProviderModel(modelId)],
            DefaultModelId = modelId,
            StructuredOutputMode = mode,
            Enabled = enabled
        };
}
