using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Tests.ViewModels;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.Views;

/// <summary>
/// GUI 冒烟（无视觉）：在 STA 线程上挂载主题资源后逐一实例化全部页面。
/// 能捕获 StaticResource 缺失、XAML 解析与页面级初始化回归；
/// 不覆盖视觉布局与交互，那部分仍属人工/Phase 后续验收。
/// </summary>
public class PageConstructionSmokeTests
{
    [Fact]
    public void AllSixPages_ConstructSuccessfully()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureThemeResources();

                AIGeekTuner.Views.Dashboard dashboard = new();
                AIGeekTuner.Views.HardwareInfoPage hardware = new();
                AIGeekTuner.Views.DiagnosisPage diagnosis = new();
                AIGeekTuner.Views.ResultPage result = new();
                AIGeekTuner.Views.DiagnosisHistoryPage history = new();
                AIGeekTuner.Views.SettingsPage settings = new();
                // V2-M4.4：SessionsPage 新增事件证据卡，纳入 XAML 解析冒烟。
                AIGeekTuner.Views.SessionsPage sessions = new();

                Assert.NotNull(dashboard);
                Assert.NotNull(hardware);
                Assert.NotNull(diagnosis);
                Assert.NotNull(result);
                Assert.NotNull(history);
                Assert.NotNull(settings);
                Assert.NotNull(sessions);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "页面构造超时");

        Assert.True(failure is null,
            "页面构造失败：" + failure?.GetType().Name + " | " + failure?.Message);
    }

    /// <summary>
    /// V2-M5.1A Gate O：设置页 Provider 卡片布局冒烟。
    /// 挂上完整 ViewModel（含 Provider 卡片），输入超长 BaseUrl / 模型 ID 后
    /// 在窄视口（约 150% DPI 下的 800px 窗口）测量排布，
    /// 确认不抛异常、PasswordBox 不含明文、页面可完整测出尺寸。
    /// </summary>
    [Fact]
    public void SettingsPage_ProviderCard_MeasuresWithoutOverflow_AtNarrowViewport()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureThemeResources();
                using var temp = new TempDirectory();

                var providerManager = new FakeAiProviderManager();
                providerManager.Profiles.Add(new AiProviderProfile
                {
                    Id = "ollama",
                    DisplayName = "Ollama（从旧设置迁移）",
                    Kind = AiProviderKind.OllamaNative,
                    BaseUrl = "http://127.0.0.1:11434",
                    Models = [new AiProviderModel("qwen3:8b")],
                    DefaultModelId = "qwen3:8b",
                    StructuredOutputMode = AiStructuredOutputMode.NativeSchema
                });
                var providerViewModel = new AiProviderSettingsViewModel(providerManager);
                // 超长内容（视觉溢出风险用例）。
                providerViewModel.BaseUrl =
                    "http://192.168.100.100:11434/with/an/extremely/long/path/segment/that/should/not/overflow";
                providerViewModel.ModelText =
                    "qwen3-omni-thinking-super-long-model-name-with-suffix:32b-instruct-fp16-awq";
                providerViewModel.DisplayName = "超长显示名称测试超长显示名称测试超长显示名称测试超长显示名称测试超长显示名称";

                var settingsViewModel = new SettingsViewModel(
                    new FakeSettingsServiceForSmoke(),
                    new FakeConnectionServiceForSmoke(),
                    new DiagnosticConfigurationStore(new DiagnosticConfiguration(
                        new OllamaOptions(), new DiagnosisInputOptions())),
                    new FakeDataDirectoryServiceForSmoke(temp.FullPath),
                    telemetryHub: null,
                    providers: providerViewModel);

                var page = new AIGeekTuner.Views.SettingsPage
                {
                    DataContext = settingsViewModel
                };

                // 约 150% DPI 下的 800px 宽度（≈533 DIP）。
                page.Measure(new Size(533, double.PositiveInfinity));
                page.Arrange(new Rect(0, 0, 533, page.DesiredSize.Height));
                page.UpdateLayout();

                Assert.True(page.DesiredSize.Width > 0);
                Assert.True(page.DesiredSize.Height > 0);

                var passwordBox = page.FindName("ProviderApiKeyBox") as PasswordBox;
                Assert.NotNull(passwordBox);
                Assert.Empty(passwordBox.Password); // 明文绝不回填到控件

                // ---- V2-M5.1A.1：ComboBox 语义与选中内容展示 ----
                var providerCombo = Assert.IsType<ComboBox>(page.FindName("ProviderSelectorCombo"));
                Assert.False(providerCombo.IsEditable); // selection-only
                // 根因修复：模板必须包含非编辑态内容展示位，闭合状态才可见。
                providerCombo.ApplyTemplate();
                var contentSite = providerCombo.Template.FindName("ContentSite", providerCombo)
                    as ContentPresenter;
                Assert.NotNull(contentSite);
                var selectionBox = Assert.IsType<AiProviderSelectorItem>(providerCombo.SelectionBoxItem);
                Assert.Equal("Ollama（从旧设置迁移）", selectionBox.Label);

                var modelCombo = Assert.IsType<ComboBox>(page.FindName("ProviderModelCombo"));
                Assert.True(modelCombo.IsEditable); // 自由输入 Model ID
                var defaultModelCombo = Assert.IsType<ComboBox>(page.FindName("DefaultModelCombo"));
                Assert.False(defaultModelCombo.IsEditable); // selection-only
                var structuredCombo = Assert.IsType<ComboBox>(page.FindName("StructuredOutputCombo"));
                Assert.False(structuredCombo.IsEditable); // selection-only

                var intervalCombo = Assert.IsType<ComboBox>(page.FindName("RecordingIntervalCombo"));
                Assert.False(intervalCombo.IsEditable); // selection-only
                var intervalItem = Assert.IsType<ComboBoxItem>(intervalCombo.SelectedItem);
                Assert.Equal("2 秒（推荐）", intervalItem.Content); // 当前值加载后直接可见
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Provider 卡片布局冒烟超时");

        Assert.True(failure is null,
            "Provider 卡片布局冒烟失败：" + failure?.GetType().Name + " | " + failure?.Message);
    }

    private sealed class FakeSettingsServiceForSmoke : IApplicationSettingsService
    {
        public ApplicationSettings Current { get; private set; } = new();

        public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConnectionServiceForSmoke : IOllamaConnectionService
    {
        public Task<OllamaReadinessResult> CheckReadinessAsync(
            string baseUrl, string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OllamaReadinessResult(
                OllamaReadinessStatus.Ready, "ready", ["qwen3:8b"]));
        }

        public Task<IReadOnlyList<string>> GetModelsAsync(
            string baseUrl, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> models = ["qwen3:8b"];
            return Task.FromResult(models);
        }
    }

    private sealed class FakeDataDirectoryServiceForSmoke : ILocalDataDirectoryService
    {
        public FakeDataDirectoryServiceForSmoke(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public void Open()
        {
        }
    }

    private static void EnsureThemeResources()
    {
        var app = Application.Current ?? new Application();

        var hasTheme = app.Resources.MergedDictionaries
            .Any(dictionary => dictionary.Source?.OriginalString
                .Contains("BlueToolboxTheme", StringComparison.OrdinalIgnoreCase) == true);
        if (!hasTheme)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/AIGeekTuner;component/Themes/BlueToolboxTheme.xaml",
                    UriKind.Absolute)
            });
        }
    }
}
