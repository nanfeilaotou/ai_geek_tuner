using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Transport;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels;

/// <summary>
/// V2-M5.1B Gate M：Active Provider（“当前使用”）语义。
/// “当前编辑”≠“当前使用”；脏草稿禁止直接设为当前使用；激活必须指向已保存 profile。
/// </summary>
public class AiProviderActiveStateTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void Constructor_ShowsResolvedActiveProviderDisplay()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
            AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b"));
        var viewModel = new AiProviderSettingsViewModel(manager);

        // 无显式 ActiveProviderId → fallback 到 ollama profile。
        Assert.Contains("Ollama", viewModel.ActiveProviderDisplay);
        Assert.Contains("qwen3:8b", viewModel.ActiveProviderDisplay);
        Assert.True(viewModel.IsEditingProfileActive);
        Assert.False(viewModel.CanActivateCurrentProfile);
    }

    [Fact]
    public void EditingNonActiveProfile_ShowsActivateButton()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
            AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b"));
        manager.Profiles.Add(MakeProfile("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1",
            AiProviderKind.OpenAiCompatible, ["m2"], "m2"));
        manager.ActiveOverride = manager.GetProfile("lmstudio");
        var viewModel = new AiProviderSettingsViewModel(manager);

        // ReloadProfiles 默认选第一个（ollama），active 是 lmstudio。
        Assert.False(viewModel.IsEditingProfileActive);
        Assert.True(viewModel.CanActivateCurrentProfile);
        Assert.Contains("LM Studio", viewModel.ActiveProviderDisplay);
    }

    [Fact]
    public void FourPersistedProfiles_ShowDeepSeekAsActiveWithoutUsingDraft()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("p1", "Ollama", "http://127.0.0.1:11434",
            AiProviderKind.OllamaNative, ["ollama-model"], "ollama-model"));
        manager.Profiles.Add(MakeProfile("p2", "LM Studio", "http://127.0.0.1:1234/v1",
            AiProviderKind.OpenAiCompatible, ["lm-model"], "lm-model"));
        manager.Profiles.Add(MakeProfile("p3", "llama.cpp", "http://127.0.0.1:8080/v1",
            AiProviderKind.OpenAiCompatible, ["llama-model"], "llama-model"));
        var deepSeek = MakeProfile("p4", "DeepSeek", "https://api.deepseek.com/v1",
            AiProviderKind.OpenAiCompatible, ["deepseek-chat", "deepseek-reasoner"], "deepseek-chat");
        manager.Profiles.Add(deepSeek);
        manager.ActiveOverride = deepSeek;

        var viewModel = new AiProviderSettingsViewModel(manager);

        Assert.Equal(4, viewModel.SelectorItems.Count);
        Assert.Contains("DeepSeek", viewModel.ActiveProviderDisplay);
        Assert.Contains("deepseek-chat", viewModel.ActiveProviderDisplay);
        Assert.False(viewModel.IsEditingProfileActive);
        Assert.True(viewModel.CanActivateCurrentProfile);
    }

    [Fact]
    public async Task Activate_WithDirtyDraft_IsRejected_WithoutManagerCall()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1",
            AiProviderKind.OpenAiCompatible, ["m2"], "m2"));
        manager.ActiveOverride = null;
        var viewModel = new AiProviderSettingsViewModel(manager);

        viewModel.DisplayName = "脏草稿 LM Studio"; // 标记 dirty

        await ((AsyncRelayCommand)viewModel.ActivateCurrentProfileCommand).ExecuteAsync();

        Assert.Empty(manager.SetActiveCalls);
        Assert.Contains("请先保存 Provider 配置，再设为当前使用。", viewModel.StatusMessage);
        Assert.Equal(AIGeekTuner.ViewModels.SettingsStatusKind.Warning, viewModel.StatusKind);
    }

    [Fact]
    public async Task Activate_WithSavedCleanProfile_CallsManager_AndRefreshes()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
            AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b"));
        manager.Profiles.Add(MakeProfile("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1",
            AiProviderKind.OpenAiCompatible, ["m2"], "m2"));
        manager.ActiveOverride = manager.GetProfile("lmstudio");
        var viewModel = new AiProviderSettingsViewModel(manager);

        await ((AsyncRelayCommand)viewModel.ActivateCurrentProfileCommand).ExecuteAsync();

        var call = Assert.Single(manager.SetActiveCalls);
        Assert.Equal("ollama", call);
        Assert.Equal(SettingsStatusKind.Success, viewModel.StatusKind);
    }

    // ============================================================
    // 真实 manager：SetActiveProviderAsync 原子持久化 ActiveProviderId
    // ============================================================

    [Fact]
    public async Task SetActiveProvider_PersistsStableId_RequiresSavedEnabledProfileWithModel()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            Profiles =
            [
                MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
                    AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b")
            ]
        });
        var manager = new AiProviderManager(
            store,
            new FakeAiCredentialStore(),
            new StubbedOpenAiClient(),
            new StubbedOllamaClient());

        var ok = await manager.SetActiveProviderAsync("ollama");
        Assert.True(ok.Success);
        Assert.Equal("ollama", store.Snapshot().ActiveProviderId);

        var missing = await manager.SetActiveProviderAsync("ghost");
        Assert.False(missing.Success);
        Assert.Contains("请先保存", missing.Error);

        // Enabled=false 的 profile 不能激活。
        store.Replace(new AiProviderConfiguration
        {
            Profiles =
            [
                MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
                    AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b", enabled: false)
            ],
            ActiveProviderId = "ollama"
        });
        var disabled = await manager.SetActiveProviderAsync("ollama");
        Assert.False(disabled.Success);
    }

    [Fact]
    public async Task SaveProfile_PreservesActiveProviderId_DeleteClearsIt()
    {
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = "ollama",
            Profiles =
            [
                MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
                    AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b"),
                MakeProfile("custom-1", "自定义", "http://127.0.0.1:9999/v1",
                    AiProviderKind.OpenAiCompatible, ["m"], "m")
            ]
        });
        var manager = new AiProviderManager(
            store,
            new FakeAiCredentialStore(),
            new StubbedOpenAiClient(),
            new StubbedOllamaClient());

        // 保存其他 profile：ActiveProviderId 保持不变。
        var draft = MakeProfile("custom-1", "自定义（改）", "http://127.0.0.1:9999/v1",
            AiProviderKind.OpenAiCompatible, ["m2"], "m2");
        var save = await manager.SaveProfileAsync(draft, AiCredentialChange.Keep);
        Assert.True(save.Success);
        Assert.Equal("ollama", store.Snapshot().ActiveProviderId);

        // 删除 active profile：ActiveProviderId 清空（下一次请求走 fallback）。
        var delete = await manager.DeleteProfileAsync("ollama");
        Assert.True(delete.Success);
        Assert.Null(store.Snapshot().ActiveProviderId);
        Assert.Single(store.Snapshot().Profiles);
    }

    private static AiProviderProfile MakeProfile(
        string id, string displayName, string baseUrl,
        AiProviderKind kind, string[] models, string defaultModel,
        bool enabled = true) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Kind = kind,
            BaseUrl = baseUrl,
            Models = models.Select(modelId => new AiProviderModel(modelId)).ToArray(),
            DefaultModelId = defaultModel,
            StructuredOutputMode = AiStructuredOutputMode.NativeSchema,
            Enabled = enabled
        };

    private sealed class StubbedOpenAiClient : IOpenAiCompatibleClient
    {
        public Task<AiModelDiscoveryResult> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiModelDiscoveryResult(AiModelDiscoveryStatus.ModelsUnavailable, "stub", []));

        public Task<AiConnectionTestResult> ProbeChatAsync(string baseUrl, string? apiKey, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConnectionTestResult(AiConnectionTestStatus.Connected, "stub"));

        public Task<AiStructuredOutputProbeResult> ProbeStructuredOutputAsync(string baseUrl, string? apiKey, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiStructuredOutputProbeResult(false, "stub"));
    }

    private sealed class StubbedOllamaClient : IOllamaNativeClient
    {
        public Task<AiModelDiscoveryResult> ListModelsAsync(string baseUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiModelDiscoveryResult(AiModelDiscoveryStatus.ModelsUnavailable, "stub", []));

        public Task<AiConnectionTestResult> ProbeChatAsync(string baseUrl, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConnectionTestResult(AiConnectionTestStatus.Connected, "stub"));

        public Task<AiStructuredOutputProbeResult> ProbeStructuredOutputAsync(string baseUrl, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiStructuredOutputProbeResult(false, "stub"));
    }

    public void Dispose() => _temp.Dispose();
}
