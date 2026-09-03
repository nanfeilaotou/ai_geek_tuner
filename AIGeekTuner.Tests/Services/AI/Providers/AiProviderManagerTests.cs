using System.IO;
using System.Net.Http;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Transport;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers;

/// <summary>
/// Gate L/M/N/O：Provider 管理器——草稿语义（绝不偷偷保存）、
/// 保存原子性与回滚、删除语义、凭据按 ProviderId 关联。
/// 全部使用 Stub HttpMessageHandler，不真实调用任何 API。
/// </summary>
public sealed class AiProviderManagerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StubAiHttpHandler _handler = new();
    private readonly WindowsDpapiCredentialStore _credentials;
    private readonly AiProviderProfileStore _store;
    private readonly AiProviderManager _manager;

    public AiProviderManagerTests()
    {
        _credentials = new WindowsDpapiCredentialStore(_temp.Combine("credentials.json"));
        _store = new AiProviderProfileStore(
            _temp.Combine("ai-providers.json"),
            new OllamaOptions { BaseUrl = "http://localhost:11434", ModelName = "qwen3:8b" });
        var openAi = new OpenAiCompatibleClient(new HttpClient(_handler));
        var ollama = new OllamaNativeClient(new HttpClient(new StubAiHttpHandler()));
        _manager = new AiProviderManager(_store, _credentials, openAi, ollama);
    }

    private static AiProviderProfile LmStudioDraft() => new()
    {
        Id = "lmstudio",
        DisplayName = "LM Studio",
        Kind = AiProviderKind.OpenAiCompatible,
        BaseUrl = "http://127.0.0.1:1234/v1",
        Models = new[] { new AiProviderModel("llama-3.1-8b") },
        DefaultModelId = "llama-3.1-8b",
        StructuredOutputMode = AiStructuredOutputMode.OpenAiJsonSchema
    };

    [Fact]
    public async Task FetchModels_UsesDraft_DoesNotSaveAnything()
    {
        _handler.EnqueueSuccessJson("""{ "data": [ { "id": "m1" }, { "id": "m2" } ] }""");
        var draft = LmStudioDraft();

        var result = await _manager.FetchModelsAsync(draft, "sk-draft-only");

        Assert.Equal(AiModelDiscoveryStatus.ModelsDiscovered, result.Status);
        Assert.Equal(new[] { "m1", "m2" }, result.Models);
        // 草稿未保存：配置文件未写、快照未变、凭据未落盘。
        Assert.False(File.Exists(_temp.Combine("ai-providers.json")));
        Assert.Null(_store.Snapshot().Profiles.FirstOrDefault(profile => profile.Id == "lmstudio"));
        Assert.Null(await _credentials.LoadAsync("lmstudio"));
    }

    [Fact]
    public async Task FetchModels_WithoutPlainKey_FallsBackToStoredCredential()
    {
        await _credentials.SaveAsync("lmstudio", "sk-stored");
        _handler.EnqueueSuccessJson("""{ "data": [ { "id": "m1" } ] }""");

        await _manager.FetchModelsAsync(LmStudioDraft());

        Assert.Equal("Bearer sk-stored", Assert.Single(_handler.Requests).Authorization);
    }

    [Fact]
    public async Task FetchModels_WithPlainKey_PrefersDraftKeyOverStored()
    {
        await _credentials.SaveAsync("lmstudio", "sk-stored");
        _handler.EnqueueSuccessJson("""{ "data": [ { "id": "m1" } ] }""");

        await _manager.FetchModelsAsync(LmStudioDraft(), "sk-draft");

        Assert.Equal("Bearer sk-draft", Assert.Single(_handler.Requests).Authorization);
    }

    [Fact]
    public async Task TestConnection_LmStudioStyleNoKey_SendsRequestWithoutAuthHeader()
    {
        // 本地服务不要求 Key：缺 Key 绝不能把 Profile 判无效，请求照发。
        _handler.EnqueueSuccessJson("""{ "choices": [ { "message": { "role": "assistant" } } ] }""");

        var result = await _manager.TestConnectionAsync(LmStudioDraft());

        Assert.Equal(AiConnectionTestStatus.Connected, result.Status);
        var request = Assert.Single(_handler.Requests);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task TestConnection_DoesNotSaveAnything()
    {
        _handler.EnqueueSuccessJson("""{ "choices": [ { "message": { } } ] }""");

        await _manager.TestConnectionAsync(LmStudioDraft(), "sk-draft-only");

        Assert.False(File.Exists(_temp.Combine("ai-providers.json")));
        Assert.Null(await _credentials.LoadAsync("lmstudio"));
    }

    [Fact]
    public async Task TestConnection_MissingModelAndDefault_MissingModelStatus()
    {
        var draft = LmStudioDraft() is { } baseDraft
            ? new AiProviderProfile
            {
                Id = baseDraft.Id,
                DisplayName = baseDraft.DisplayName,
                Kind = baseDraft.Kind,
                BaseUrl = baseDraft.BaseUrl,
                Models = baseDraft.Models,
                DefaultModelId = null,
                StructuredOutputMode = baseDraft.StructuredOutputMode
            }
            : throw new InvalidOperationException();

        var result = await _manager.TestConnectionAsync(draft);

        Assert.Equal(AiConnectionTestStatus.MissingModel, result.Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Save_CommitsProfileCredential_AndReplacesSnapshot()
    {
        var draft = LmStudioDraft();

        var result = await _manager.SaveProfileAsync(
            draft,
            new AiCredentialChange(AiCredentialChangeMode.Replace, "sk-live"));

        Assert.True(result.Success);
        Assert.NotNull(_manager.GetProfile("lmstudio"));
        Assert.Equal(2, _manager.ListProfiles().Count); // 迁移出的 ollama + lmstudio
        Assert.True(File.Exists(_temp.Combine("ai-providers.json")));
        Assert.Equal("sk-live", await _credentials.LoadAsync("lmstudio"));

        // 持久化文件里只有非敏感配置；凭据在独立文件。
        var providersJson = await File.ReadAllTextAsync(_temp.Combine("ai-providers.json"));
        Assert.DoesNotContain("sk-live", providersJson);
    }

    [Fact]
    public async Task Save_DisplayRename_KeepsSameProviderIdentity()
    {
        await _manager.SaveProfileAsync(LmStudioDraft(), AiCredentialChange.Keep);
        var renamed = new AiProviderProfile
        {
            Id = "lmstudio",
            DisplayName = "改名后的 LM Studio",
            Kind = AiProviderKind.OpenAiCompatible,
            BaseUrl = "http://127.0.0.1:1234/v1",
            Models = new[] { new AiProviderModel("llama-3.1-8b") },
            DefaultModelId = "llama-3.1-8b",
            StructuredOutputMode = AiStructuredOutputMode.OpenAiJsonSchema
        };

        var result = await _manager.SaveProfileAsync(renamed, AiCredentialChange.Keep);

        Assert.True(result.Success);
        var profile = Assert.Single(
            _manager.ListProfiles(),
            candidate => candidate.Id == "lmstudio");
        Assert.Equal("改名后的 LM Studio", profile.DisplayName);
    }

    [Fact]
    public async Task Save_InvalidDraft_KeepsOldSnapshotAndCredential()
    {
        await _manager.SaveProfileAsync(LmStudioDraft(), AiCredentialChange.Keep);
        var snapshotBefore = _store.Snapshot();

        var broken = LmStudioDraft() is { } baseDraft
            ? new AiProviderProfile
            {
                Id = baseDraft.Id,
                DisplayName = baseDraft.DisplayName,
                Kind = baseDraft.Kind,
                BaseUrl = "not-a-url",
                Models = baseDraft.Models,
                DefaultModelId = baseDraft.DefaultModelId,
                StructuredOutputMode = baseDraft.StructuredOutputMode
            }
            : throw new InvalidOperationException();
        var result = await _manager.SaveProfileAsync(broken, AiCredentialChange.Keep);

        Assert.False(result.Success);
        Assert.Same(snapshotBefore, _store.Snapshot());
    }

    [Fact]
    public async Task Save_ConfigWriteFails_RollsBackCredential_NoHalfAppliedState()
    {
        // 用一个“保存必失败”的 store 验证回滚：凭据先写成功，配置写失败 → 凭据必须被回滚。
        var failingStore = new FailingSaveProviderStore(new AiProviderConfiguration());
        var manager = new AiProviderManager(
            failingStore,
            _credentials,
            new OpenAiCompatibleClient(new HttpClient(_handler)),
            new OllamaNativeClient(new HttpClient(new StubAiHttpHandler())));

        var result = await manager.SaveProfileAsync(
            LmStudioDraft(),
            new AiCredentialChange(AiCredentialChangeMode.Replace, "sk-should-not-survive"));

        Assert.False(result.Success);
        Assert.Null(await _credentials.LoadAsync("lmstudio"));
        Assert.Empty(Directory.GetFiles(_temp.FullPath, "ai-providers.json"));
    }

    [Fact]
    public async Task Save_ReplaceCredential_ThenDeleteCredential()
    {
        await _manager.SaveProfileAsync(LmStudioDraft(), new AiCredentialChange(AiCredentialChangeMode.Replace, "sk-a"));
        Assert.Equal("sk-a", await _credentials.LoadAsync("lmstudio"));

        var result = await _manager.SaveProfileAsync(LmStudioDraft(), new AiCredentialChange(AiCredentialChangeMode.Delete));

        Assert.True(result.Success);
        Assert.Null(await _credentials.LoadAsync("lmstudio"));
        Assert.NotNull(_manager.GetProfile("lmstudio"));
    }

    [Fact]
    public async Task Delete_RemovesProfileAndCredential_KeepOthersIntact()
    {
        await _manager.SaveProfileAsync(LmStudioDraft(), new AiCredentialChange(AiCredentialChangeMode.Replace, "sk-a"));
        var ollamaBefore = _manager.GetProfile("ollama");

        var result = await _manager.DeleteProfileAsync("lmstudio");

        Assert.True(result.Success);
        Assert.Null(_manager.GetProfile("lmstudio"));
        Assert.Null(await _credentials.LoadAsync("lmstudio"));
        Assert.Same(ollamaBefore, _manager.GetProfile("ollama"));
    }

    [Fact]
    public async Task Delete_UnknownProvider_Fails()
    {
        var result = await _manager.DeleteProfileAsync("ghost");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task StructuredOutputMode_Persists_AcrossReload()
    {
        var draft = LmStudioDraft();
        draft = new AiProviderProfile
        {
            Id = draft.Id,
            DisplayName = draft.DisplayName,
            Kind = draft.Kind,
            BaseUrl = draft.BaseUrl,
            Models = draft.Models,
            DefaultModelId = draft.DefaultModelId,
            StructuredOutputMode = AiStructuredOutputMode.JsonObject
        };
        await _manager.SaveProfileAsync(draft, AiCredentialChange.Keep);

        var reloaded = new AiProviderProfileStore(_temp.Combine("ai-providers.json"));

        var profile = reloaded.Snapshot().Profiles.Single(candidate => candidate.Id == "lmstudio");
        Assert.Equal(AiStructuredOutputMode.JsonObject, profile.StructuredOutputMode);
    }

    [Fact]
    public async Task ValidateDraft_SameIdAsExisting_IsAnEdit_NotAConflict()
    {
        await _manager.SaveProfileAsync(LmStudioDraft(), AiCredentialChange.Keep);

        // 同 Id 的草稿是“编辑既有 Provider”，不是 Id 冲突；显示名改变不影响身份。
        Assert.Empty(_manager.ValidateDraft(LmStudioDraft()));
    }

    /// <summary>保存时永远失败的 store 替身（模拟磁盘 IO 故障）。</summary>
    private sealed class FailingSaveProviderStore : IAiProviderProfileStore
    {
        public FailingSaveProviderStore(AiProviderConfiguration snapshot)
        {
            SnapshotValue = snapshot;
        }

        public AiProviderConfiguration SnapshotValue { get; }

        public AiProviderConfiguration Snapshot() => SnapshotValue;

        public Task SaveAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken = default)
        {
            throw new AiProviderStoreException("模拟磁盘写入失败。");
        }
    }

    public void Dispose() => _temp.Dispose();
}
