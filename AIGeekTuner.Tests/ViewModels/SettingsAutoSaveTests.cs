using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using AIGeekTuner.Views;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels;

/// <summary>
/// M5.2G/H/J：普通设置自动保存契约 —— 开关/下拉立即持久化、文本去抖、
/// 失焦/Enter 提交、非法值不覆盖持久化值、全局 Save 移除、
/// Provider 保持显式 Save、auto-save 后 export 立即包含新值。
/// </summary>
public class SettingsAutoSaveTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSettingsService _settingsService = new();
    private readonly DiagnosticConfigurationStore _store =
        new(new DiagnosticConfiguration(new OllamaOptions(), new DiagnosisInputOptions()));

    // ---- 9) 开关变更自动持久化 ----

    [Fact]
    public async Task ToggleChange_AutoSavesImmediately()
    {
        var viewModel = CreateViewModel();

        viewModel.AutoSaveDiagnosisHistory = false;
        await viewModel.CommitAutoSaveAsync();

        var saved = Assert.Single(_settingsService.SavedValues);
        Assert.False(saved.AutoSaveDiagnosisHistory);
    }

    [Fact]
    public async Task VoiceToggleChange_AutoSavesImmediately()
    {
        var viewModel = CreateViewModel();

        viewModel.VoiceEnabled = true;
        await viewModel.CommitAutoSaveAsync();

        var saved = Assert.Single(_settingsService.SavedValues);
        Assert.True(saved.Voice.Enabled);
    }

    // ---- 10) 下拉选择自动持久化 ----

    [Fact]
    public async Task ComboSelection_AutoSavesImmediately()
    {
        var viewModel = CreateViewModel();

        viewModel.RecordingIntervalMs = 5000;
        viewModel.VoicePromptLang = "en";
        await viewModel.CommitAutoSaveAsync();

        // 每次开关/下拉变更都立即落盘一次；最终值 = 最后一次保存。
        Assert.Equal(2, _settingsService.SavedValues.Count);
        var saved = _settingsService.SavedValues[^1];
        Assert.Equal(5000, saved.RecordingIntervalMs);
        Assert.Equal("en", saved.Voice.PromptLang);
    }

    // ---- 11) 合法数字输入持久化（去抖后提交）----

    [Fact]
    public async Task ValidNumericInput_PersistsAfterCommit()
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

    // ---- 14) 非法输入不覆盖持久化有效值 ----

    [Fact]
    public async Task InvalidInput_DoesNotOverwritePersistedValue()
    {
        var original = new ApplicationSettings { OllamaTimeoutSeconds = 300 };
        _settingsService.Load(original);
        var viewModel = CreateViewModel();

        viewModel.TimeoutSecondsText = "abc";
        await viewModel.CommitAutoSaveAsync();

        Assert.Empty(_settingsService.SavedValues);
        Assert.Same(original, _settingsService.Current);
        Assert.NotEqual(SettingsStatusKind.Success, viewModel.StatusKind);
        Assert.Contains("自动保存已暂停", viewModel.StatusMessage);
    }

    [Fact]
    public async Task OutOfRangeInput_DoesNotOverwritePersistedValue()
    {
        var original = new ApplicationSettings { OllamaTimeoutSeconds = 300 };
        _settingsService.Load(original);
        var viewModel = CreateViewModel();

        viewModel.TimeoutSecondsText = "999999";
        await viewModel.CommitAutoSaveAsync();

        Assert.Empty(_settingsService.SavedValues);
        Assert.Same(original, _settingsService.Current);
    }

    // ---- 15) 快速连续输入合并写盘（去抖计时器真实走一次）----

    [Fact]
    public void RepeatedFastTyping_CoalescesIntoSingleWrite()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewModel = CreateViewModel();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                viewModel.TimeoutSecondsText = "600";
                Pump(stopwatch, 250);
                viewModel.TimeoutSecondsText = "700";
                Pump(stopwatch, 250);
                // 两次输入间隔都小于 400ms 去抖：此时还不得写盘。
                Assert.Empty(_settingsService.SavedValues);
                Pump(stopwatch, 500);

                Assert.Single(_settingsService.SavedValues);
                Assert.Equal(700, _settingsService.SavedValues[0].OllamaTimeoutSeconds);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "debounce coalesce test 超时");
        Assert.Null(failure);
    }

    // ---- 12) LostFocus 提交 / 13) Enter 提交（接线契约）----

    [Fact]
    public void LostFocusHandler_CommitsDirtyDraft()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewModel = CreateViewModel();
                viewModel.TimeoutSecondsText = "600";

                var textBox = new TextBox { DataContext = viewModel };
                textBox.RaiseEvent(new KeyboardFocusChangedEventArgs(
                    null, 0, null, null)
                {
                    RoutedEvent = Keyboard.LostKeyboardFocusEvent
                });
                var committed = viewModel.CommitAutoSaveAsync();
                // 失焦处理器已消费 dirty；等待链尾完成即可。
                committed.Wait(TimeSpan.FromSeconds(10));

                Assert.Single(_settingsService.SavedValues);
                Assert.Equal(600, _settingsService.SavedValues[0].OllamaTimeoutSeconds);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "lost focus commit test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void FocusAndEnterHandlers_AreWiredOnSettingsPage()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(Path.Combine("AIGeekTuner", "Views", "SettingsPage.xaml")));
        var code = File.ReadAllText(FindRepositoryFile(Path.Combine("AIGeekTuner", "Views", "SettingsPage.xaml.cs")));

        Assert.Contains("LostKeyboardFocus=\"SettingsInput_LostKeyboardFocus\"", xaml);
        Assert.Contains("PreviewKeyDown=\"SettingsInput_PreviewKeyDown\"", xaml);
        Assert.Contains("CommitAutoSaveAsync", code);
        Assert.Contains("Key.Enter", code);
    }

    // ---- 16) 路径类设置持久化（当前 UI 为文本输入；无文件选择器）----

    [Fact]
    public async Task VoiceReferenceAudioPath_PersistsOnCommit()
    {
        var viewModel = CreateViewModel();

        viewModel.VoiceReferenceAudioPath = "D:\\audio\\ref-new.wav";
        await viewModel.CommitAutoSaveAsync();

        var saved = Assert.Single(_settingsService.SavedValues);
        Assert.Equal("D:\\audio\\ref-new.wav", saved.Voice.ReferenceAudioPath);
    }

    // ---- 17) 全局 Save 移除 / 18) Provider Save 保留 ----

    [Fact]
    public void GlobalSaveButton_IsRemovedFromSettingsPage()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(Path.Combine("AIGeekTuner", "Views", "SettingsPage.xaml")));
        var viewModel = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "ViewModels", "SettingsViewModel.cs")));

        Assert.DoesNotContain("Content=\"保存设置\"", xaml);
        Assert.DoesNotContain("SaveCommand", xaml);
        Assert.DoesNotContain("SaveCommand", viewModel);
        Assert.DoesNotContain("_saveCommand", viewModel);
        // 轻量状态反馈仍在（自动保存 / 保存失败）。
        Assert.Contains("StatusMessage", xaml);
        Assert.Contains("已自动保存", viewModel);
    }

    [Fact]
    public void ProviderSaveButton_RemainsExplicit()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(Path.Combine("AIGeekTuner", "Views", "SettingsPage.xaml")));

        Assert.Contains("保存 Provider", xaml);
        Assert.Contains("Providers.SaveProviderCommand", xaml);
        Assert.Contains("测试连接", xaml);
        Assert.Contains("设为当前使用", xaml);
    }

    // ---- 19) Provider 草稿不被自动保存写入 ----

    [Fact]
    public void AutoSaveCore_NeverTouchesProviderDraftState()
    {
        var source = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "ViewModels", "SettingsViewModel.cs")));

        var coreStart = source.IndexOf("private async Task RunAutoSaveCoreAsync", StringComparison.Ordinal);
        var coreEnd = source.IndexOf("private async Task ExportSettingsAsync", StringComparison.Ordinal);
        Assert.True(coreStart >= 0 && coreEnd > coreStart);
        var core = source.Substring(coreStart, coreEnd - coreStart);
        Assert.DoesNotContain("Providers.", core);
        Assert.DoesNotContain("providerStore", core);
        // 自动保存只写应用设置 + 运行时配置快照。
        Assert.Contains("_settingsService.SaveAsync", core);
        Assert.Contains("ReplaceRuntimeConfiguration", core);
    }

    // ---- 20) auto-save 后立刻 export，导出即新值 ----

    [Fact]
    public async Task AutoSavedSettings_AppearInExportV2Immediately()
    {
        var settingsPath = _temp.Combine("settings.json");
        var exportPath = _temp.Combine("export-v2.json");
        var jsonService = new JsonApplicationSettingsService(settingsPath);
        var store = new DiagnosticConfigurationStore(
            new DiagnosticConfiguration(new OllamaOptions(), new DiagnosisInputOptions()));
        var viewModel = new SettingsViewModel(
            jsonService,
            store,
            new FakeDataDirectoryService(_temp.Combine("data")),
            settingsPortability: new ApplicationSettingsPortabilityService(
                jsonService,
                settingsPath));

        viewModel.TimeoutSecondsText = "777";
        await viewModel.CommitAutoSaveAsync();

        await new ApplicationSettingsPortabilityService(jsonService, settingsPath)
            .ExportAsync(exportPath);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(777, root.GetProperty("settings").GetProperty("aiTimeoutSeconds").GetInt32());
    }

    private SettingsViewModel CreateViewModel() =>
        new(
            _settingsService,
            _store,
            new FakeDataDirectoryService(_temp.Combine("data")));

    private static void Pump(System.Diagnostics.Stopwatch stopwatch, int milliseconds)
    {
        var deadline = stopwatch.ElapsedMilliseconds + milliseconds;
        while (stopwatch.ElapsedMilliseconds < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(20);
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var candidate = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var probe = Path.GetFullPath(Path.Combine(candidate, relativePath));
            if (File.Exists(probe))
            {
                return probe;
            }

            candidate = Path.GetDirectoryName(candidate)!;
        }

        throw new FileNotFoundException($"无法定位测试文件 {relativePath}");
    }

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
