using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.ViewModels;
using AIGeekTuner.Views.Behaviors;
using Xunit;

namespace AIGeekTuner.Tests.Views;

[CollectionDefinition("WpfSmoke", DisableParallelization = true)]
public sealed class WpfSmokeCollection
{
}

[Collection("WpfSmoke")]
public sealed class WindowChromeTests
{
    [Fact]
    public void MainWindowXaml_UsesWindowChromeAndCustomCaption()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml")));

        Assert.Contains("WindowStyle=\"None\"", xaml);
        Assert.Contains("ResizeMode=\"CanResize\"", xaml);
        Assert.Contains("shell:WindowChrome.WindowChrome", xaml);
        Assert.Contains("x:Name=\"TitleBar\"", xaml);
        Assert.Contains("x:Name=\"MinimizeButton\"", xaml);
        Assert.Contains("x:Name=\"MaximizeButton\"", xaml);
        Assert.Contains("x:Name=\"CloseButton\"", xaml);
        Assert.DoesNotContain("WindowStyle=\"SingleBorderWindow\"", xaml);
    }

    [Fact]
    public void CaptionButtons_ExposeAccessibleNamesAndHandlers()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml")));

        Assert.Contains("AutomationProperties.Name=\"最小化\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"最大化 / 还原\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"关闭\"", xaml);
        Assert.Contains("Click=\"MinimizeButton_Click\"", xaml);
        Assert.Contains("Click=\"MaximizeButton_Click\"", xaml);
        Assert.Contains("Click=\"CloseButton_Click\"", xaml);
        Assert.Contains("StateChanged=\"Window_StateChanged\"", xaml);
    }

    [Fact]
    public void MainWindow_ConstructsOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureThemeResources();
                var window = new MainWindow();
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.Equal(1024, window.MinWidth);
                Assert.Equal(680, window.MinHeight);
                Assert.NotNull(window.FindName("TitleBar"));
                Assert.NotNull(window.FindName("MinimizeButton"));
                Assert.NotNull(window.FindName("MaximizeButton"));
                Assert.NotNull(window.FindName("CloseButton"));
                var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                Assert.Equal("仪表盘", viewModel.CurrentPageDisplayName);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "MainWindow 构造超时");
        Assert.Null(failure);
    }

    [Fact]
    public void CaptionActions_MinimizeMaximizeRestoreDeterministically()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window { WindowState = WindowState.Normal };

                WindowChromeController.Minimize(window);
                Assert.Equal(WindowState.Minimized, window.WindowState);

                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Maximized, window.WindowState);

                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "caption action test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void MaximizeGlyphState_FollowsWindowStateWithoutPolling()
    {
        Assert.False(WindowChromeController.IsMaximized(WindowState.Normal));
        Assert.True(WindowChromeController.IsMaximized(WindowState.Maximized));
        Assert.False(WindowChromeController.IsMaximized(WindowState.Minimized));
    }

    [Fact]
    public void DragHitTest_ExcludesInteractiveControlsAndAllowsBlankBorder()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var blank = new Border();
                var button = new Button();
                var textBox = new TextBox();
                var comboBox = new ComboBox();
                var listBox = new ListBox();
                var dataGrid = new DataGrid();
                var scrollBar = new ScrollBar();

                Assert.False(WindowDragHitTest.IsInteractive(blank));
                Assert.True(WindowDragHitTest.IsInteractive(button));
                Assert.True(WindowDragHitTest.IsInteractive(textBox));
                Assert.True(WindowDragHitTest.IsInteractive(comboBox));
                Assert.True(WindowDragHitTest.IsInteractive(listBox));
                Assert.True(WindowDragHitTest.IsInteractive(dataGrid));
                Assert.True(WindowDragHitTest.IsInteractive(scrollBar));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "drag hit-test test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void MainWindowViewModel_DisplayTitleFollowsCurrentPage()
    {
        var navigation = new FakeNavigationService();
        var viewModel = new MainWindowViewModel(navigation);

        ((System.Windows.Input.ICommand)viewModel.ShowDiagnosisCommand).Execute(null);
        Assert.Equal(AppPage.Diagnosis.ToString(), viewModel.CurrentPageName);
        Assert.Equal("AI 智能诊断", viewModel.CurrentPageDisplayName);

        ((System.Windows.Input.ICommand)viewModel.ShowSettingsCommand).Execute(null);
        Assert.Equal("设置", viewModel.CurrentPageDisplayName);

        navigation.Publish(AppPage.Result);
        Assert.Equal("诊断报告", viewModel.CurrentPageDisplayName);
    }

    private sealed class FakeNavigationService : INavigationService, IPageNavigationNotifications
    {
        public bool CanGoBack => false;
        public bool IsCurrent(AppPage page) => false;
        public event Action<AppPage>? CurrentPageChanged;
        public void NavigateTo(AppPage page, object? parameter = null) { }
        public void GoBack() { }

        public void Publish(AppPage page) => CurrentPageChanged?.Invoke(page);
    }

    private static void EnsureThemeResources()
    {
        var app = Application.Current ?? new Application();
        if (app.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString.Contains("BlueToolboxTheme", StringComparison.OrdinalIgnoreCase) == true))
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/AIGeekTuner;component/Themes/BlueToolboxTheme.xaml",
                UriKind.Absolute)
        });
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
}
