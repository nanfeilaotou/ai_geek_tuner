using System.IO;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Transport;
using AIGeekTuner.Services.Dialogs;
using AIGeekTuner.Tests.Services.AI.Providers;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels;

/// <summary>
/// “AI 服务提供方”卡片视图模型测试（V2-M5.1A Gate N）。
/// 全部通过 FakeAiProviderManager / 真实 store + Stub HTTP 完成，绝不访问真实网络；
/// 覆盖：草稿语义、凭据三态、模型合并、四态发现反馈、六态连接反馈、
/// 保存/删除原子语义、dirty 切换保护、密钥永不出现在展示属性。
/// </summary>
public class AiProviderSettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    // xUnit 每个测试方法新建实例，实例字段可安全承载当前测试的 manager 引用。
    private FakeAiProviderManager? _currentFakeManager;

    // ============================================================
    // 1. 加载迁移出的 Ollama profile
    // ============================================================

    [Fact]
    public async Task Constructor_LoadsMigratedOllamaProfile_FromLegacySettings()
    {
        var fixture = CreateRealManager(new OllamaOptions
        {
            BaseUrl = "http://192.168.1.77:11434",
            ModelName = "qwen2.5:14b"
        });
        var viewModel = new AiProviderSettingsViewModel(fixture.Manager);

        Assert.Equal("ollama", viewModel.SelectedItem?.ExistingProfileId);
        Assert.Equal("ollama", viewModel.ProviderIdDisplay);
        Assert.Contains("Ollama", viewModel.DisplayName);
        Assert.Equal("http://192.168.1.77:11434", viewModel.BaseUrl);
        Assert.Equal("Ollama Native", viewModel.KindDisplay);
        Assert.Equal("qwen2.5:14b", viewModel.ModelText);
        Assert.Equal("qwen2.5:14b", viewModel.DefaultModelText);
        Assert.Contains("qwen2.5:14b", viewModel.ModelOptions);
        Assert.Equal(AiStructuredOutputMode.NativeSchema, viewModel.SelectedStructuredOutputMode);
        Assert.False(viewModel.HasUnsavedChanges);

        await viewModel.RefreshCredentialStatusAsync();
        Assert.Equal("未配置", viewModel.CredentialStatusText);
    }

    // ============================================================
    // 2. 选择 profile → 草稿字段加载
    // ============================================================

    [Fact]
    public void SelectProfile_LoadsDraftFields()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
            AiProviderKind.OllamaNative, ["qwen3:8b"], "qwen3:8b"));
        manager.Profiles.Add(MakeProfile("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1",
            AiProviderKind.OpenAiCompatible, ["loaded-model"], "loaded-model"));
        var viewModel = new AiProviderSettingsViewModel(manager);

        var second = viewModel.SelectorItems.First(item => item.ExistingProfileId == "lmstudio");
        viewModel.SelectedItem = second;

        Assert.Equal("lmstudio", viewModel.ProviderIdDisplay);
        Assert.Equal("LM Studio", viewModel.DisplayName);
        Assert.Equal("http://127.0.0.1:1234/v1", viewModel.BaseUrl);
        Assert.Equal("loaded-model", viewModel.ModelText);
        Assert.Equal("OpenAI Compatible", viewModel.KindDisplay);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    // ============================================================
    // 3. 编辑 DisplayName 不改变 Id，并标记 dirty
    // ============================================================

    [Fact]
    public void EditDisplayName_KeepsProviderId_AndMarksDirty()
    {
        var viewModel = CreateVmWithSingleProfile();
        var originalId = viewModel.ProviderIdDisplay;

        viewModel.DisplayName = "改过名字的 Ollama";

        Assert.Equal(originalId, viewModel.ProviderIdDisplay);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    // ============================================================
    // 4. LM Studio 预设
    // ============================================================

    [Fact]
    public void LmStudioPreset_CreatesDraft_WithPresetDefaults()
    {
        var viewModel = CreateVmWithSingleProfile();

        viewModel.SelectedItem = CreationEntry(viewModel, AiProviderCreationPreset.LmStudio);

        Assert.True(viewModel.IsNewDraft);
        Assert.Equal("OpenAI Compatible", viewModel.KindDisplay);
        Assert.Equal(AiProviderPresets.LmStudioDefaultBaseUrl, viewModel.BaseUrl);
        Assert.Equal(AiStructuredOutputMode.OpenAiJsonSchema, viewModel.SelectedStructuredOutputMode);
        Assert.Equal("LM Studio", viewModel.DisplayName);
    }

    // ============================================================
    // 5. 自定义预设：Id 冲突时自动递增
    // ============================================================

    [Fact]
    public void CustomPreset_GeneratesUniqueId_WhenBaseIdTaken()
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("custom-1", "已有自定义", "http://127.0.0.1:8080/v1",
            AiProviderKind.OpenAiCompatible, [], null));
        var viewModel = new AiProviderSettingsViewModel(manager);

        viewModel.SelectedItem = CreationEntry(viewModel, AiProviderCreationPreset.CustomOpenAiCompatible);

        Assert.Equal("custom-2", viewModel.ProviderIdDisplay);
        Assert.Equal(AiStructuredOutputMode.PromptOnly, viewModel.SelectedStructuredOutputMode);
    }

    // ============================================================
    // 6. 已存凭据显示“已配置”，绝不显示明文
    // ============================================================

    [Fact]
    public async Task StoredCredential_ShowsConfigured_NeverPlaintext()
    {
        const string secret = "sk-real-secret-123";
        var fixture = CreateRealManager(new OllamaOptions());
        await fixture.Credentials.SaveAsync("ollama", secret);
        var viewModel = new AiProviderSettingsViewModel(
            fixture.Manager,
            null,
            async providerId => await fixture.Credentials.LoadAsync(providerId) is not null);

        await viewModel.RefreshCredentialStatusAsync();

        Assert.True(viewModel.HasStoredCredential);
        Assert.Equal("已配置（不显示明文）", viewModel.CredentialStatusText);
        AssertCredentialNeverExposed(viewModel, secret);
    }

    // ============================================================
    // 7. 未输入密钥时保存 = KeepExisting
    // ============================================================

    [Fact]
    public async Task Save_WithoutKeyInput_KeepsExistingCredential()
    {
        var viewModel = CreateVmWithSingleProfile(credentialExists: _ => false);

        await viewModel.SaveProviderCommand.ExecuteAsync();

        var save = Assert.Single(((FakeAiProviderManager)TestManager(viewModel)!).SaveCalls);
        Assert.Equal(AiCredentialChangeMode.KeepExisting, save.Change.Mode);
    }

    // ============================================================
    // 8. 输入新密钥后保存 = Replace
    // ============================================================

    [Fact]
    public async Task Save_WithTypedKey_ReplacesCredential()
    {
        var viewModel = CreateVmWithSingleProfile(credentialExists: _ => true);
        viewModel.SetApiKeyInput("brand-new-key");

        await viewModel.SaveProviderCommand.ExecuteAsync();

        var save = Assert.Single(((FakeAiProviderManager)TestManager(viewModel)!).SaveCalls);
        Assert.Equal(AiCredentialChangeMode.Replace, save.Change.Mode);
        Assert.Equal("brand-new-key", save.Change.PlainText);
    }

    // ============================================================
    // 9. 清除后保存 = Delete
    // ============================================================

    [Fact]
    public async Task ClearKey_Save_DeletesCredential()
    {
        var viewModel = CreateVmWithSingleProfile(credentialExists: _ => true);
        await viewModel.RefreshCredentialStatusAsync();
        viewModel.ClearKeyCommand.Execute(null);
        Assert.Contains("清除", viewModel.CredentialStatusText);

        await viewModel.SaveProviderCommand.ExecuteAsync();

        var save = Assert.Single(((FakeAiProviderManager)TestManager(viewModel)!).SaveCalls);
        Assert.Equal(AiCredentialChangeMode.Delete, save.Change.Mode);
    }

    // ============================================================
    // 10. 刷新模型使用未保存的 BaseUrl / 输入中的 Key
    // ============================================================

    [Fact]
    public async Task FetchModels_UsesUnsavedBaseUrl_AndTypedKey()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        viewModel.BaseUrl = "http://10.1.2.3:9999/v1";
        viewModel.SetApiKeyInput("typed-key");

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.Equal("http://10.1.2.3:9999/v1", Assert.Single(manager.FetchDrafts).BaseUrl);
        Assert.Equal("typed-key", Assert.Single(manager.FetchKeys));
    }

    // ============================================================
    // 11. 刷新模型绝不保存
    // ============================================================

    [Fact]
    public async Task FetchModels_DoesNotPersistAnything()
    {
        var fixture = CreateRealManager(new OllamaOptions());
        var viewModel = new AiProviderSettingsViewModel(fixture.Manager);
        viewModel.BaseUrl = "http://127.0.0.1:65000";

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.False(File.Exists(fixture.ProvidersPath));
        Assert.False(File.Exists(fixture.CredentialsPath));
        Assert.Single(fixture.Manager.ListProfiles());
        Assert.False(viewModel.HasStoredCredential);
    }

    // ============================================================
    // 12. /models 不可用仍允许手动模型（Gate F 文案）
    // ============================================================

    [Fact]
    public async Task FetchModels_Unavailable_AllowsManualModel_AndSave()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.FetchResult = (_, _) => new AiModelDiscoveryResult(
            AiModelDiscoveryStatus.ModelsUnavailable, "发现失败", []);
        viewModel.ModelText = "my-manual-model";

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.Equal("该服务未提供可用的模型列表，可手动输入模型 ID。", viewModel.StatusMessage);
        Assert.Equal(SettingsStatusKind.Warning, viewModel.StatusKind);
        Assert.Equal("my-manual-model", viewModel.ModelText);

        await viewModel.SaveProviderCommand.ExecuteAsync();

        var save = Assert.Single(manager.SaveCalls);
        Assert.Contains(save.Draft.Models, model => model.Id == "my-manual-model");
    }

    // ============================================================
    // 13. 发现 + 手动模型合并（手动条目保留、不重复）
    // ============================================================

    [Fact]
    public async Task FetchModels_MergesDiscovered_KeepsManual_NoDuplicates()
    {
        var viewModel = CreateVmWithSingleProfile(); // baseline 模型 my-model
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.FetchResult = (_, _) => new AiModelDiscoveryResult(
            AiModelDiscoveryStatus.ModelsDiscovered,
            "ok",
            ["a-model", "b-model", "my-model"]);

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.Equal(["my-model", "a-model", "b-model"], viewModel.ModelOptions);
        Assert.True(viewModel.HasUnsavedChanges);
    }

    // ============================================================
    // 14. 重复模型被阻止
    // ============================================================

    [Fact]
    public async Task FetchModels_DuplicateCandidates_AreNotAddedTwice()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.FetchResult = (_, _) => new AiModelDiscoveryResult(
            AiModelDiscoveryStatus.ModelsDiscovered, "ok", ["my-model", "my-model", "My-Model"]);

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.Single(viewModel.ModelOptions);

        viewModel.ModelText = "MY-MODEL";
        await viewModel.SaveProviderCommand.ExecuteAsync();
        var save = Assert.Single(manager.SaveCalls);
        Assert.Single(save.Draft.Models);
    }

    // ============================================================
    // 15. 测试连接使用当前草稿
    // ============================================================

    [Fact]
    public async Task TestConnection_UsesDraftBaseUrlModelAndKey()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        viewModel.BaseUrl = "http://10.9.9.9:8080/v1";
        viewModel.ModelText = "test-target";
        viewModel.SetApiKeyInput("conn-key");

        await viewModel.TestConnectionCommand.ExecuteAsync();

        var call = Assert.Single(manager.TestCalls);
        Assert.Equal("http://10.9.9.9:8080/v1", call.Draft.BaseUrl);
        Assert.Equal("test-target", call.Model);
        Assert.Equal("conn-key", call.Key);
    }

    // ============================================================
    // 16. 测试连接绝不保存
    // ============================================================

    [Fact]
    public async Task TestConnection_DoesNotPersistAnything()
    {
        var fixture = CreateRealManager(new OllamaOptions());
        var viewModel = new AiProviderSettingsViewModel(fixture.Manager);
        viewModel.ModelText = "qwen2.5:14b";

        await viewModel.TestConnectionCommand.ExecuteAsync();

        Assert.False(File.Exists(fixture.ProvidersPath));
        Assert.False(File.Exists(fixture.CredentialsPath));
        Assert.Single(fixture.Manager.ListProfiles());
    }

    // ============================================================
    // 17-19. 连接测试状态文案
    // ============================================================

    [Fact]
    public async Task TestConnection_AuthenticationFailed_ShowsChineseMessage()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.TestResult = (_, _, _) => new AiConnectionTestResult(
            AiConnectionTestStatus.AuthenticationFailed, "401");

        await viewModel.TestConnectionCommand.ExecuteAsync();

        Assert.Equal("认证失败，请检查 API Key。", viewModel.StatusMessage);
        Assert.Equal(SettingsStatusKind.Error, viewModel.StatusKind);
    }

    [Fact]
    public async Task TestConnection_ModelMissing_ShowsWarning()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.TestResult = (_, _, _) => new AiConnectionTestResult(
            AiConnectionTestStatus.MissingModel, "missing");

        await viewModel.TestConnectionCommand.ExecuteAsync();

        Assert.Equal("请先选择或输入模型 ID。", viewModel.StatusMessage);
        Assert.Equal(SettingsStatusKind.Warning, viewModel.StatusKind);
    }

    [Fact]
    public async Task TestConnection_ConnectionUnavailable_ShowsError()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.TestResult = (_, _, _) => new AiConnectionTestResult(
            AiConnectionTestStatus.ConnectionUnavailable, "down");

        await viewModel.TestConnectionCommand.ExecuteAsync();

        Assert.Equal("无法连接到服务。", viewModel.StatusMessage);
        Assert.Equal(SettingsStatusKind.Error, viewModel.StatusKind);
    }

    // ============================================================
    // 20. 结构化输出探测：结果不影响草稿模式
    // ============================================================

    [Fact]
    public async Task StructuredProbe_Unsupported_DoesNotChangeDraftMode()
    {
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;
        manager.StructuredResult = (_, _, _) => new AiStructuredOutputProbeResult(false, "not supported");

        await viewModel.TestStructuredOutputCommand.ExecuteAsync();

        Assert.Contains("不影响 Provider 配置", viewModel.StructuredProbeText);
        Assert.Equal(AiStructuredOutputMode.NativeSchema, viewModel.SelectedStructuredOutputMode);
        Assert.Equal(AiStructuredOutputMode.NativeSchema, Assert.Single(manager.StructuredCalls).Draft.StructuredOutputMode);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    // ============================================================
    // 21. 保存成功：快照与选择器刷新、文件落盘
    // ============================================================

    [Fact]
    public async Task Save_Success_RefreshesSelector_AndPersists()
    {
        var fixture = CreateRealManager(new OllamaOptions());
        var viewModel = new AiProviderSettingsViewModel(
            fixture.Manager,
            null,
            async providerId => await fixture.Credentials.LoadAsync(providerId) is not null);

        viewModel.SelectedItem = CreationEntry(viewModel, AiProviderCreationPreset.LmStudio);
        viewModel.BaseUrl = "http://127.0.0.1:1234/v1";
        viewModel.ModelText = "loaded-model";

        await viewModel.SaveProviderCommand.ExecuteAsync();

        Assert.Equal(SettingsStatusKind.Success, viewModel.StatusKind);
        Assert.Equal("Provider 配置已保存。", viewModel.StatusMessage);
        Assert.True(File.Exists(fixture.ProvidersPath));
        var profiles = fixture.Manager.ListProfiles();
        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, profile => profile.Id == "lmstudio");
        Assert.Equal("lmstudio", viewModel.SelectedItem?.ExistingProfileId);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains(viewModel.SelectorItems, item => item.ExistingProfileId == "lmstudio");
    }

    // ============================================================
    // 22. 保存失败：草稿保留、已持久化状态不变
    // ============================================================

    [Fact]
    public async Task Save_Failure_KeepsDraft_AndPersistedState()
    {
        var inner = new AiProviderProfileStore(
            _temp.Combine("ai-providers.json"),
            new OllamaOptions { BaseUrl = "http://127.0.0.1:11434", ModelName = "qwen3:8b" });
        var store = new FailingSaveProviderStore(inner);
        var credentials = new WindowsDpapiCredentialStore(_temp.Combine("credentials.json"));
        var manager = new AiProviderManager(
            store,
            credentials,
            new OpenAiCompatibleClient(new System.Net.Http.HttpClient(new StubAiHttpHandler())),
            new OllamaNativeClient(new System.Net.Http.HttpClient(new StubAiHttpHandler())));
        var viewModel = new AiProviderSettingsViewModel(manager);
        var originalDisplayName = viewModel.DisplayName;

        viewModel.DisplayName = "改坏的名字";
        viewModel.ModelText = "qwen3:8b";
        await viewModel.SaveProviderCommand.ExecuteAsync();

        Assert.Equal(SettingsStatusKind.Error, viewModel.StatusKind);
        Assert.Equal("改坏的名字", viewModel.DisplayName);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal(originalDisplayName, manager.GetProfile("ollama")?.DisplayName);
        Assert.False(File.Exists(_temp.Combine("ai-providers.json")));
        Assert.False(File.Exists(_temp.Combine("credentials.json")));
    }

    // ============================================================
    // 23. 删除：profile + 凭据一起消失
    // ============================================================

    [Fact]
    public async Task Delete_RemovesProfile_AndCredential()
    {
        var fixture = CreateRealManager(legacyOllamaOptions: null);
        var draft = MakeProfile("custom-1", "自定义", "http://127.0.0.1:8080/v1",
            AiProviderKind.OpenAiCompatible, ["m1"], "m1");
        var saveResult = await fixture.Manager.SaveProfileAsync(
            draft, new AiCredentialChange(AiCredentialChangeMode.Replace, "sk-delete-me"));
        Assert.True(saveResult.Success);

        var dialog = new FakeConfirmationDialog { Answer = true };
        var viewModel = new AiProviderSettingsViewModel(fixture.Manager, dialog);
        Assert.Equal("custom-1", viewModel.SelectedItem?.ExistingProfileId);

        await viewModel.DeleteProviderCommand.ExecuteAsync();

        Assert.Empty(fixture.Manager.ListProfiles());
        Assert.Null(await fixture.Credentials.LoadAsync("custom-1"));
        Assert.Equal(3, viewModel.SelectorItems.Count); // 只剩 3 个创建入口
        Assert.Null(viewModel.SelectedItem);
        Assert.True(dialog.ConfirmCalls >= 1);
        Assert.Contains("凭据也会删除", dialog.ConfirmMessages[0]);
    }

    // ============================================================
    // 24. dirty 时切换 Provider 的保护
    // ============================================================

    [Fact]
    public async Task SwitchProfile_WithDirtyDraft_RequiresConfirmation()
    {
        var dialog = new FakeConfirmationDialog();
        var viewModel = CreateVmWithSingleProfile(dialog: dialog);
        viewModel.DisplayName = "未保存的名字";
        var originalItem = viewModel.SelectedItem;

        dialog.Answer = false;
        viewModel.SelectedItem = CreationEntry(viewModel, AiProviderCreationPreset.LmStudio);
        Assert.Same(originalItem, viewModel.SelectedItem);
        Assert.Equal("未保存的名字", viewModel.DisplayName);
        Assert.Equal(1, dialog.ConfirmCalls);

        dialog.Answer = true;
        viewModel.SelectedItem = CreationEntry(viewModel, AiProviderCreationPreset.LmStudio);
        Assert.NotSame(originalItem, viewModel.SelectedItem);
        Assert.True(viewModel.IsNewDraft);
        Assert.Equal("LM Studio", viewModel.DisplayName);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(2, dialog.ConfirmCalls);

        await Task.CompletedTask;
    }

    // ============================================================
    // 25. API Key 绝不出现在任何展示属性
    // ============================================================

    [Fact]
    public async Task ApiKey_IsNeverExposed_InDisplayProperties()
    {
        const string secret = "SUPER-SECRET-KEY-VALUE";
        var viewModel = CreateVmWithSingleProfile();
        var manager = (FakeAiProviderManager)TestManager(viewModel)!;

        viewModel.SetApiKeyInput(secret);
        await viewModel.RefreshModelsCommand.ExecuteAsync();
        await viewModel.TestConnectionCommand.ExecuteAsync();

        // 内部传给 manager 的明文是允许的（这是它的唯一去向）。
        Assert.Equal(secret, Assert.Single(manager.FetchKeys));
        AssertCredentialNeverExposed(viewModel, secret);
    }

    // ============================================================
    // 辅助
    // ============================================================

    private FakeAiProviderManager TestManager(AiProviderSettingsViewModel viewModel) =>
        _currentFakeManager ?? throw new InvalidOperationException("测试未通过 CreateVmWithSingleProfile 创建。");

    private static void AssertCredentialNeverExposed(AiProviderSettingsViewModel viewModel, string secret)
    {
        foreach (var property in viewModel.GetType().GetProperties())
        {
            if (property.PropertyType == typeof(string))
            {
                var value = (string?)property.GetValue(viewModel);
                Assert.DoesNotContain(secret, value);
            }
        }

        foreach (var option in viewModel.ModelOptions)
        {
            Assert.DoesNotContain(secret, option);
        }

        foreach (var item in viewModel.SelectorItems)
        {
            Assert.DoesNotContain(secret, item.Label);
        }
    }

    private static AiProviderSelectorItem CreationEntry(
        AiProviderSettingsViewModel viewModel,
        AiProviderCreationPreset preset)
    {
        return viewModel.SelectorItems.First(item => item.CreationPreset == preset);
    }

    private AiProviderSettingsViewModel CreateVmWithSingleProfile(
        FakeConfirmationDialog? dialog = null,
        Func<string, bool>? credentialExists = null)
    {
        var manager = new FakeAiProviderManager();
        manager.Profiles.Add(MakeProfile("ollama", "Ollama", "http://127.0.0.1:11434",
            AiProviderKind.OllamaNative, ["my-model"], "my-model"));
        _currentFakeManager = manager;
        return new AiProviderSettingsViewModel(
            manager,
            dialog,
            credentialExists is null ? null : id => Task.FromResult(credentialExists(id)));
    }

    private RealProviderFixture CreateRealManager(OllamaOptions? legacyOllamaOptions)
    {
        var providersPath = _temp.Combine("ai-providers.json");
        var credentialsPath = _temp.Combine("credentials.json");
        var credentials = new WindowsDpapiCredentialStore(credentialsPath);
        var store = new AiProviderProfileStore(providersPath, legacyOllamaOptions);
        var manager = new AiProviderManager(
            store,
            credentials,
            new OpenAiCompatibleClient(new System.Net.Http.HttpClient(new StubAiHttpHandler())),
            new OllamaNativeClient(new System.Net.Http.HttpClient(new StubAiHttpHandler())));
        return new RealProviderFixture(manager, providersPath, credentialsPath, credentials);
    }

    private sealed record RealProviderFixture(
        AiProviderManager Manager,
        string ProvidersPath,
        string CredentialsPath,
        WindowsDpapiCredentialStore Credentials);

    private static AiProviderProfile MakeProfile(
        string id,
        string displayName,
        string baseUrl,
        AiProviderKind kind,
        IReadOnlyList<string> models,
        string? defaultModelId)
    {
        return new AiProviderProfile
        {
            Id = id,
            DisplayName = displayName,
            Kind = kind,
            BaseUrl = baseUrl,
            Models = models.Select(modelId => new AiProviderModel(modelId)).ToArray(),
            DefaultModelId = defaultModelId,
            StructuredOutputMode = kind == AiProviderKind.OllamaNative
                ? AiStructuredOutputMode.NativeSchema
                : AiStructuredOutputMode.PromptOnly,
            Enabled = true
        };
    }

    public void Dispose() => _temp.Dispose();
}

/// <summary>脚本化的 IAiProviderManager 替身：记录草稿输入，按脚本返回结果。</summary>
internal sealed class FakeAiProviderManager : IAiProviderManager
{
    public List<AiProviderProfile> Profiles { get; } = [];

    public List<AiProviderProfile> FetchDrafts { get; } = [];
    public List<string?> FetchKeys { get; } = [];
    public List<(AiProviderProfile Draft, string? Key, string? Model)> TestCalls { get; } = [];
    public List<(AiProviderProfile Draft, string? Key, string? Model)> StructuredCalls { get; } = [];
    public List<(AiProviderProfile Draft, AiCredentialChange Change)> SaveCalls { get; } = [];
    public List<string> DeleteCalls { get; } = [];

    public Func<AiProviderProfile, string?, AiModelDiscoveryResult>? FetchResult { get; set; }
    public Func<AiProviderProfile, string?, string?, AiConnectionTestResult>? TestResult { get; set; }
    public Func<AiProviderProfile, string?, string?, AiStructuredOutputProbeResult>? StructuredResult { get; set; }

    public AiProviderSaveResult NextSaveResult { get; set; } = AiProviderSaveResult.Ok();

    public IReadOnlyList<AiProviderProfile> ListProfiles() => Profiles;

    public AiProviderProfile? GetProfile(string providerId) =>
        Profiles.FirstOrDefault(profile => profile.Id == providerId);

    public IReadOnlyList<string> ValidateDraft(AiProviderProfile draft)
    {
        var errors = new List<string>(AiProviderProfileValidator.Validate(draft));
        if (Profiles.Any(profile =>
                !string.Equals(profile.Id, draft.Id, StringComparison.Ordinal)
                && string.Equals(profile.Id, draft.Id, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Provider ID {draft.Id} 已被其他 Provider 使用。");
        }

        return errors;
    }

    public Task<AiModelDiscoveryResult> FetchModelsAsync(
        AiProviderProfile draft, string? plainApiKey = null, CancellationToken cancellationToken = default)
    {
        FetchDrafts.Add(draft);
        FetchKeys.Add(plainApiKey);
        var result = FetchResult?.Invoke(draft, plainApiKey)
            ?? new AiModelDiscoveryResult(
                AiModelDiscoveryStatus.ModelsDiscovered, "ok", []);
        return Task.FromResult(result);
    }

    public Task<AiConnectionTestResult> TestConnectionAsync(
        AiProviderProfile draft, string? plainApiKey = null, string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        TestCalls.Add((draft, plainApiKey, modelId));
        var result = TestResult?.Invoke(draft, plainApiKey, modelId)
            ?? new AiConnectionTestResult(AiConnectionTestStatus.Connected, "ok");
        return Task.FromResult(result);
    }

    public Task<AiStructuredOutputProbeResult> TestStructuredOutputAsync(
        AiProviderProfile draft, string? plainApiKey = null, string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        StructuredCalls.Add((draft, plainApiKey, modelId));
        var result = StructuredResult?.Invoke(draft, plainApiKey, modelId)
            ?? new AiStructuredOutputProbeResult(true, "ok");
        return Task.FromResult(result);
    }

    public Task<AiProviderSaveResult> SaveProfileAsync(
        AiProviderProfile draft, AiCredentialChange credentialChange,
        CancellationToken cancellationToken = default)
    {
        SaveCalls.Add((draft, credentialChange));
        if (!NextSaveResult.Success)
        {
            return Task.FromResult(NextSaveResult);
        }

        Profiles.RemoveAll(profile => profile.Id == draft.Id);
        Profiles.Add(draft);
        return Task.FromResult(NextSaveResult);
    }

    public Task<AiProviderSaveResult> DeleteProfileAsync(
        string providerId, CancellationToken cancellationToken = default)
    {
        DeleteCalls.Add(providerId);
        Profiles.RemoveAll(profile => profile.Id == providerId);
        return Task.FromResult(AiProviderSaveResult.Ok());
    }
}

/// <summary>可编程应答的确认对话框替身。</summary>
internal sealed class FakeConfirmationDialog : IConfirmationDialogService
{
    public bool Answer { get; set; } = true;

    public int ConfirmCalls { get; private set; }

    public List<string> ConfirmMessages { get; } = [];

    public bool Confirm(string title, string message)
    {
        ConfirmCalls++;
        ConfirmMessages.Add(message);
        return Answer;
    }
}

/// <summary>SaveAsync 必失败的 store 包装：用于验证保存失败时草稿与磁盘状态都不变。</summary>
internal sealed class FailingSaveProviderStore : IAiProviderProfileStore
{
    private readonly IAiProviderProfileStore _inner;

    public FailingSaveProviderStore(IAiProviderProfileStore inner)
    {
        _inner = inner;
    }

    public AiProviderConfiguration Snapshot() => _inner.Snapshot();

    public Task SaveAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken = default)
    {
        throw new AiProviderStoreException("注入的保存失败（测试用）。");
    }
}
