using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels;

/// <summary>
/// SettingsViewModel 关键逻辑（V2-M5.1B 后）：草稿加载、保存校验与快照替换、
/// 旧 Ollama 字段的数据兼容（UI 已移除但持久化保留）、自动保存串行化（M5.2G）。
/// </summary>
public class SettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSettingsService _settingsService = new();
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

        Assert.Equal("600", viewModel.TimeoutSecondsText);
        Assert.Equal("8,000", viewModel.MaxLogLengthText);
        Assert.False(viewModel.AutoSaveDiagnosisHistory);
    }

    [Fact]
    public async Task Save_Success_PersistsSwapsStoreAndReportsSuccess()
    {
        var viewModel = CreateViewModel();
        viewModel.TimeoutSecondsText = "600";
        viewModel.MaxLogLengthText = "8000";

        await viewModel.CommitAutoSaveAsync();

        var saved = Assert.Single(_settingsService.SavedValues);
        Assert.Equal(600, saved.OllamaTimeoutSeconds);
        Assert.Equal(8000, saved.MaxFaultLogCharacters);
        Assert.Equal(600, _store.Snapshot().Ollama.TimeoutSeconds);
        Assert.Equal(SettingsStatusKind.Success, viewModel.StatusKind);
    }

    [Fact]
    public async Task Save_PreservesLegacyOllamaFields_NoLongerEditableInUi()
    {
        // Gate N：旧 Ollama 字段从 UI 移除，但保存时必须原样保留（升级兼容），
        // 且 ConfigurationStore 快照继续携带它们（legacy 读取方不破）。
        _settingsService.Load(new ApplicationSettings
        {
            OllamaBaseUrl = "http://192.168.1.50:11434",
            OllamaModelName = "llama3.1:8b",
            UseJsonFormat = false
        });
        var viewModel = CreateViewModel();
        viewModel.TimeoutSecondsText = "600";

        await viewModel.CommitAutoSaveAsync();

        var saved = Assert.Single(_settingsService.SavedValues);
        Assert.Equal("http://192.168.1.50:11434", saved.OllamaBaseUrl);
        Assert.Equal("llama3.1:8b", saved.OllamaModelName);
        Assert.False(saved.UseJsonFormat);
        Assert.Equal("http://192.168.1.50:11434", _store.Snapshot().Ollama.BaseUrl);
        Assert.Equal("llama3.1:8b", _store.Snapshot().Ollama.ModelName);
        Assert.False(_store.Snapshot().Ollama.UseJsonFormat);
    }

    [Fact]
    public async Task Save_NonNumericTimeout_ShowsValidationError()
    {
        var viewModel = CreateViewModel();
        viewModel.TimeoutSecondsText = "abc";

        await viewModel.CommitAutoSaveAsync();

        Assert.Contains("整数", viewModel.StatusMessage);
        Assert.Empty(_settingsService.SavedValues);
    }
    [Fact]
    public async Task AutoSave_ChangesDuringInFlightSave_AreSerializedByChain()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsService.GateSave = gate;
        var viewModel = CreateViewModel();

        viewModel.TimeoutSecondsText = "600";
        var first = viewModel.CommitAutoSaveAsync();

        // 保存进行中再变更草稿：链式排队，绝不并发写盘。
        viewModel.TimeoutSecondsText = "900";
        var second = viewModel.CommitAutoSaveAsync();

        gate.TrySetResult();
        await first;
        await second;

        Assert.Equal(2, _settingsService.SavedValues.Count);
        Assert.Equal(600, _settingsService.SavedValues[0].OllamaTimeoutSeconds);
        Assert.Equal(900, _settingsService.SavedValues[1].OllamaTimeoutSeconds);
        Assert.Equal(900, _settingsService.Current.OllamaTimeoutSeconds);
    }

    private SettingsViewModel CreateViewModel() =>
        new(
            _settingsService,
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