using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels;

/// <summary>
/// SettingsViewModel 关键逻辑：草稿加载、保存校验与快照替换、
/// 模型刷新不覆盖手输、三态连接反馈、保存期重入保护。
/// </summary>
public class SettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSettingsService _settingsService = new();
    private readonly FakeConnectionService _connectionService = new();
    private readonly DiagnosticConfigurationStore _store =
        new(new DiagnosticConfiguration(new OllamaOptions(), new DiagnosisInputOptions()));

    [Fact]
    public void Constructor_LoadsDraftsFromPersistedSettings()
    {
        _settingsService.Load(new ApplicationSettings
        {
            OllamaBaseUrl = "http://192.168.1.50:11434",
            OllamaModelName = "llama3.1:8b",
            OllamaTimeoutSeconds = 600,
            MaxFaultLogCharacters = 8_000,
            UseJsonFormat = false,
            AutoSaveDiagnosisHistory = false
        });
        var viewModel = CreateViewModel();

        Assert.Equal("http://192.168.1.50:11434", viewModel.BaseUrl);
        Assert.Equal("llama3.1:8b", viewModel.ModelName);
        Assert.Equal("600", viewModel.TimeoutSecondsText);
        Assert.Equal("8,000", viewModel.MaxLogLengthText);
        Assert.False(viewModel.UseJsonFormat);
        Assert.False(viewModel.AutoSaveDiagnosisHistory);
    }

    [Fact]
    public async Task Save_Success_PersistsSwapsStoreAndReportsSuccess()
    {
        var viewModel = CreateViewModel();
        viewModel.BaseUrl = "http://192.168.1.50:11434";
        viewModel.ModelName = "llama3.1:8b";
        viewModel.TimeoutSecondsText = "600";
        viewModel.MaxLogLengthText = "8000";
        viewModel.UseJsonFormat = false;

        await viewModel.SaveCommand.ExecuteAsync();

        var saved = Assert.Single(_settingsService.SavedValues);
        Assert.Equal("llama3.1:8b", saved.OllamaModelName);
        Assert.False(saved.UseJsonFormat);
        Assert.Equal("llama3.1:8b", _store.Snapshot().Ollama.ModelName);
        Assert.False(_store.Snapshot().Ollama.UseJsonFormat);
        Assert.Equal(SettingsStatusKind.Success, viewModel.StatusKind);
    }

    [Fact]
    public async Task Save_InvalidBaseUrl_ShowsErrorWithoutSideEffects()
    {
        var viewModel = CreateViewModel();
        viewModel.BaseUrl = "not-a-url";

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Equal(SettingsStatusKind.Warning, viewModel.StatusKind);
        Assert.Contains("服务地址", viewModel.StatusMessage);
        Assert.Empty(_settingsService.SavedValues);
        Assert.Equal(OllamaOptions.DefaultModelName, _store.Snapshot().Ollama.ModelName);
    }

    [Fact]
    public async Task Save_NonNumericTimeout_ShowsValidationError()
    {
        var viewModel = CreateViewModel();
        viewModel.TimeoutSecondsText = "abc";

        await viewModel.SaveCommand.ExecuteAsync();

        Assert.Contains("整数", viewModel.StatusMessage);
        Assert.Empty(_settingsService.SavedValues);
    }

    [Fact]
    public async Task RefreshModels_FillsListAndKeepsManuallyTypedModelName()
    {
        var viewModel = CreateViewModel();
        viewModel.ModelName = "my-future-model";

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.AvailableModels.Count);
        Assert.Equal("my-future-model", viewModel.ModelName);
    }

    [Fact]
    public async Task RefreshModels_Offline_ShowsFriendlyWarning()
    {
        var viewModel = CreateViewModel();
        _connectionService.OnGetModelsException =
            new OllamaConnectionException("Ollama 服务未启动或无法连接。");

        await viewModel.RefreshModelsCommand.ExecuteAsync();

        Assert.Equal(SettingsStatusKind.Warning, viewModel.StatusKind);
        Assert.Contains("未启动或无法连接", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TestConnection_ModelMissing_ShowsWarningWithModelName()
    {
        var viewModel = CreateViewModel();
        viewModel.ModelName = "gemma2:9b";
        _connectionService.ReadinessResult = new OllamaReadinessResult(
            OllamaReadinessStatus.ModelMissing,
            "已连接 Ollama，但未找到模型 gemma2:9b。请先执行 ollama pull 或刷新模型列表。",
            ["qwen3:8b"]);

        await viewModel.TestConnectionCommand.ExecuteAsync();

        Assert.Equal(SettingsStatusKind.Warning, viewModel.StatusKind);
        Assert.Contains("未找到模型 gemma2:9b", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TestConnection_Offline_ShowsErrorStatus()
    {
        var viewModel = CreateViewModel();
        _connectionService.ReadinessResult = new OllamaReadinessResult(
            OllamaReadinessStatus.ServiceUnavailable,
            "Ollama 服务未启动或无法连接。",
            []);

        await viewModel.TestConnectionCommand.ExecuteAsync();

        Assert.Equal(SettingsStatusKind.Error, viewModel.StatusKind);
    }

    [Fact]
    public async Task Save_WhileSaving_IgnoresSecondInvocation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.GateSave = gate;
        var viewModel = CreateViewModel();

        var first = viewModel.SaveCommand.ExecuteAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!viewModel.IsSaving && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(viewModel.IsSaving);

        // 保存进行中：第二次触发应因 CanExecute=false 直接被忽略。
        await viewModel.SaveCommand.ExecuteAsync();

        gate.TrySetResult();
        await first;

        Assert.Single(_settingsService.SavedValues);
        Assert.False(viewModel.IsSaving);
    }

    private SettingsViewModel CreateViewModel() =>
        new(
            _settingsService,
            _connectionService,
            _store,
            new FakeDataDirectoryService(_temp.Combine("data")));

    private sealed class FakeSettingsService : IApplicationSettingsService
    {
        public ApplicationSettings Current { get; private set; } = new();

        public List<ApplicationSettings> SavedValues { get; } = [];

        public TaskCompletionSource? GateSave;

        public void Load(ApplicationSettings settings)
        {
            Current = settings;
        }

        public async Task SaveAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (GateSave is not null)
            {
                await GateSave.Task;
            }

            SavedValues.Add(settings);
            Current = settings;
        }
    }

    private sealed class FakeConnectionService : IOllamaConnectionService
    {
        public OllamaReadinessResult ReadinessResult { get; set; } =
            new(OllamaReadinessStatus.Ready, "Ollama 已就绪 · qwen3:8b", ["qwen3:8b"]);

        public IReadOnlyList<string> Models { get; set; } = ["qwen3:8b", "llama3.1:8b"];

        public Exception? OnGetModelsException;

        public Task<OllamaReadinessResult> CheckReadinessAsync(
            string baseUrl,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReadinessResult);
        }

        public Task<IReadOnlyList<string>> GetModelsAsync(
            string baseUrl,
            CancellationToken cancellationToken = default)
        {
            if (OnGetModelsException is not null)
            {
                throw OnGetModelsException;
            }

            return Task.FromResult(Models);
        }
    }

    private sealed class FakeDataDirectoryService : ILocalDataDirectoryService
    {
        public FakeDataDirectoryService(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public void Open()
        {
        }
    }

    public void Dispose() => _temp.Dispose();
}
