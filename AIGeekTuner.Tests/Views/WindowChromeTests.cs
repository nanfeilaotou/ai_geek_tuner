using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
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
        Assert.Contains("ResizeMode=\"NoResize\"", xaml);
        Assert.Contains("shell:WindowChrome.WindowChrome", xaml);
        Assert.Contains("ResizeBorderThickness=\"0\"", xaml);
        Assert.Contains("CornerRadius=\"8\"", xaml);
        Assert.DoesNotContain("SourceInitialized=\"Window_SourceInitialized\"", xaml);
        Assert.Contains("x:Name=\"TitleBar\"", xaml);
        Assert.Contains("x:Name=\"MinimizeButton\"", xaml);
        Assert.Contains("x:Name=\"MaximizeButton\"", xaml);
        Assert.Contains("x:Name=\"CloseButton\"", xaml);
        Assert.DoesNotContain("PreviewMouseLeftButtonDown=\"Window_PreviewMouseLeftButtonDown\"", xaml);
        Assert.Contains("WindowChromeHitTestRouter", File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "Behaviors", "WindowChromeHitTestRouter.cs"))));
        Assert.DoesNotContain("WindowDragRegion.IsDragRegion=\"True\"", xaml);
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
        Assert.Equal(3, CountOccurrences(xaml, "Style=\"{StaticResource WindowCaptionButtonStyle}\""));
        Assert.Equal(3, CountOccurrences(xaml, "Grid Width=\"14\" Height=\"14\""));
        Assert.DoesNotContain("WindowCaptionCloseButtonStyle", xaml);

        var theme = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Themes", "BlueToolboxTheme.xaml")));
        Assert.Contains("<Setter Property=\"Width\" Value=\"46\"/>", theme);
        Assert.Contains("<Setter Property=\"Height\" Value=\"40\"/>", theme);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0\"/>", theme);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\"/>", theme);
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
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal(1024, window.MinWidth);
                Assert.Equal(680, window.MinHeight);
                Assert.Equal(1180, window.Width);
                Assert.Equal(760, window.Height);
                Assert.IsType<Border>(window.FindName("TitleBar"));
                Assert.NotNull(window.FindName("MinimizeButton"));
                Assert.NotNull(window.FindName("MaximizeButton"));
                Assert.NotNull(window.FindName("CloseButton"));
                var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                Assert.Equal("仪表盘", viewModel.CurrentPageDisplayName);

                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                var routerField = typeof(MainWindow).GetField(
                    "_windowChromeHitTestRouter",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var router = Assert.IsType<WindowChromeHitTestRouter>(routerField?.GetValue(window));
                Assert.True(router.IsAttached);
                var frame = Assert.IsType<Frame>(window.FindName("MainFrame"));
                var dashboardPage = Assert.IsType<AIGeekTuner.Views.Dashboard>(frame.Content);
                var dashboardTitle = FindVisual<TextBlock>(dashboardPage,
                    block => block.Text == "仪表盘");
                Assert.NotNull(dashboardTitle);
                Assert.True(WindowDragHitTest.IsDraggableFrom(dashboardTitle, window));

                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(1180, window.Width);
                Assert.Equal(760, window.Height);

                // Even if code temporarily changes bounds, the Normal restore
                // contract remains the original 1180x760 DIP design size.
                window.Width = 900;
                window.Height = 700;
                WindowChromeController.ToggleMaximize(window);
                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(1180, window.Width);
                Assert.Equal(760, window.Height);
                window.Close();
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
    public void DwmCornerHelper_FailureIsNonFatalAndRequiresAnHwnd()
    {
        Assert.False(WindowCornerController.TryApplyRoundedCorners(IntPtr.Zero));

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                Assert.False(WindowCornerController.TryApplyRoundedCorners(window));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "DWM corner test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void NativeHitTestRouter_MapsBackgroundToCaptionAndControlsToClient()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new Grid();
                var background = new Border();
                var button = new Button();
                root.Children.Add(background);
                root.Children.Add(button);

                Assert.Equal(
                    WindowChromeHitTestRouter.HtCaption,
                    WindowChromeHitTestRouter.Classify(background, root).ToInt32());
                Assert.Equal(
                    WindowChromeHitTestRouter.HtClient,
                    WindowChromeHitTestRouter.Classify(button, root).ToInt32());
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "native hit-test router test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void NativeHitTestRouter_LeavesUnrelatedMessagesUntouched()
    {
        var router = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "Behaviors", "WindowChromeHitTestRouter.cs")));
        Assert.Contains("message == WmNcHitTest", router);
        Assert.DoesNotContain("WM_SIZE", router);
        Assert.DoesNotContain("WM_DPICHANGED", router);
        Assert.DoesNotContain("DragMove", router);
    }

    [Fact]
    public void NativeHitTestRouter_UsesSubclassHelpersWithoutReplacingWndProc()
    {
        var router = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "Behaviors", "WindowChromeHitTestRouter.cs")));

        Assert.Contains("SetWindowSubclass", router);
        Assert.Contains("RemoveWindowSubclass", router);
        Assert.Contains("DefSubclassProc", router);
        Assert.DoesNotContain("SetWindowLongPtr", router);
        Assert.DoesNotContain("CallWindowProc", router);
        Assert.Contains("WmNcDestroy", router);
    }

    [Fact]
    public void WindowMoveState_IsInheritableToSidebarControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                var button = new Button();
                window.Content = button;
                WindowMoveState.SetIsMoving(window, true);

                Assert.True(WindowMoveState.GetIsMoving(button));
                WindowMoveState.SetIsMoving(window, false);
                Assert.False(WindowMoveState.GetIsMoving(button));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WindowMoveState test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void SidebarHoverPolicy_UsesMoveTargetInsteadOfStaleMouseOver()
    {
        Assert.True(SidebarHoverState.IsHoverActive(
            isMoving: false,
            isMouseOver: true,
            isMoveHovered: false));
        Assert.False(SidebarHoverState.IsHoverActive(
            isMoving: true,
            isMouseOver: true,
            isMoveHovered: false));
        Assert.True(SidebarHoverState.IsHoverActive(
            isMoving: true,
            isMouseOver: true,
            isMoveHovered: true));
        Assert.False(SidebarHoverState.IsHoverActive(
            isMoving: true,
            isMouseOver: false,
            isMoveHovered: false));
    }

    [Fact]
    public void SidebarMoveHoverTarget_FollowsScreenCursorAndClearsOutside()
    {
        var a = new DependencyObject();
        var b = new DependencyObject();
        var c = new DependencyObject();
        var items = new (DependencyObject Item, Rect Bounds)[]
        {
            (a, new Rect(0, 0, 100, 40)),
            (b, new Rect(0, 40, 100, 40)),
            (c, new Rect(0, 80, 100, 40))
        };

        Assert.Same(a, SidebarMoveHoverTracker.SelectTarget(new Point(20, 20), items));
        Assert.Same(b, SidebarMoveHoverTracker.SelectTarget(new Point(20, 60), items));
        Assert.Same(c, SidebarMoveHoverTracker.SelectTarget(new Point(20, 100), items));
        Assert.Null(SidebarMoveHoverTracker.SelectTarget(new Point(200, 100), items));
    }

    [Fact]
    public void SidebarMoveHoverTracker_UpdatesOnlyTheCurrentNavigationItem()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new StackPanel();
                var a = new Button { Tag = "A", Height = 30 };
                var b = new Button { Tag = "B", Height = 30 };
                var c = new Button { Tag = "C", Height = 30 };
                panel.Children.Add(a);
                panel.Children.Add(b);
                panel.Children.Add(c);

                var window = new Window
                {
                    Content = panel,
                    Width = 160,
                    Height = 140,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 100,
                    Top = 100
                };
                window.Show();
                window.UpdateLayout();

                var tracker = new SidebarMoveHoverTracker(panel);
                tracker.BeginTracking(a.PointToScreen(new Point(5, 5)));
                Assert.True(SidebarHoverState.GetIsMoveHovered(a));
                Assert.False(SidebarHoverState.GetIsMoveHovered(b));
                Assert.False(SidebarHoverState.GetIsMoveHovered(c));

                tracker.UpdateFromScreenPoint(b.PointToScreen(new Point(5, 5)));
                Assert.False(SidebarHoverState.GetIsMoveHovered(a));
                Assert.True(SidebarHoverState.GetIsMoveHovered(b));
                Assert.False(SidebarHoverState.GetIsMoveHovered(c));

                tracker.UpdateFromScreenPoint(new Point(-10000, -10000));
                Assert.False(SidebarHoverState.GetIsMoveHovered(a));
                Assert.False(SidebarHoverState.GetIsMoveHovered(b));
                Assert.False(SidebarHoverState.GetIsMoveHovered(c));
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "sidebar move-hover tracker test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void SidebarHoverState_IsNotACommandOrNavigationState()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "Behaviors", "SidebarHoverState.cs")));
        Assert.DoesNotContain("Navigate", source);
        Assert.DoesNotContain("ICommand", source);
    }

    [Fact]
    public void NativeHitTestRouter_TracksMovingCursorAndSidebarState()
    {
        var router = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "Behaviors", "WindowChromeHitTestRouter.cs")));
        Assert.Contains("WmMoving", router);
        Assert.Contains("GetCursorPos", router);
        Assert.Contains("BeginTracking", router);
        Assert.Contains("UpdateFromScreenPoint", router);
        Assert.Contains("WmExitSizeMove", router);
    }

    [Fact]
    public void CaptionButtonStyle_DoesNotDependOnSidebarMoveHover()
    {
        var theme = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Themes", "BlueToolboxTheme.xaml")));
        var captionStart = theme.IndexOf("x:Key=\"WindowCaptionButtonStyle\"", StringComparison.Ordinal);
        Assert.True(captionStart >= 0);
        Assert.DoesNotContain(
            "SidebarHoverState",
            theme.Substring(captionStart));
    }

    [Fact]
    public void NavigationHover_IsSuppressedOnlyWhileWindowMoves()
    {
        var theme = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Themes", "BlueToolboxTheme.xaml")));

        Assert.Contains("WindowMoveState.IsMoving", theme);
        Assert.Contains("Path=IsMouseOver", theme);
        Assert.Contains("Value=\"False\"", theme);
        Assert.Contains("NavigationSelectedableStyle", theme);
    }

    [Fact]
    public void MainWindow_UsesNativeHitTestAndNoWpfDragWorkaround()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml")));
        var code = File.ReadAllText(FindRepositoryFile(
            Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml.cs")));

        Assert.DoesNotContain("PreviewMouseLeftButtonDown", xaml);
        Assert.DoesNotContain("DragMove", code);
        Assert.Contains("OnSourceInitialized", code);
        Assert.Contains("WindowChromeHitTestRouter", code);
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
    public void GlobalDragHitTest_AllowsLayoutAndDisplayElements()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new Grid();
                var card = new Border
                {
                    Child = new Grid()
                };
                var cardGrid = (Grid)card.Child;
                var text = new TextBlock { Text = "13th Gen Intel(R) Core(TM) i9-13980HX" };
                cardGrid.Children.Add(text);
                root.Children.Add(card);

                var label = new Label { Content = "温度" };
                root.Children.Add(label);

                var scrollText = new TextBlock { Text = "普通信息文字" };
                var scrollGrid = new Grid();
                scrollGrid.Children.Add(scrollText);
                root.Children.Add(new ScrollViewer { Content = scrollGrid });

                Assert.True(WindowDragHitTest.IsDraggableFrom(card, root));
                Assert.True(WindowDragHitTest.IsDraggableFrom(cardGrid, root));
                Assert.True(WindowDragHitTest.IsDraggableFrom(text, root));
                Assert.True(WindowDragHitTest.IsDraggableFrom(label, root));
                Assert.True(WindowDragHitTest.IsDraggableFrom(scrollText, root));
                Assert.False(WindowDragHitTest.IsInteractive(new ScrollViewer()));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "global drag allow test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void GlobalDragHitTest_ExcludesInteractiveControlFamilies()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var controls = new DependencyObject[]
                {
                    new Button(),
                    new ToggleButton(),
                    new TextBox(),
                    new PasswordBox(),
                    new ComboBox(),
                    new ListBoxItem(),
                    new ListViewItem(),
                    new TreeViewItem(),
                    new DataGrid(),
                    new DataGridRow(),
                    new DataGridCell(),
                    new DataGridColumnHeader(),
                    new ScrollBar(),
                    new Thumb(),
                    new Slider(),
                    new CheckBox(),
                    new RadioButton(),
                    new Menu(),
                    new MenuItem(),
                    new Hyperlink(),
                    new Expander(),
                    new TabItem()
                };

                foreach (var control in controls)
                {
                    Assert.True(
                        WindowDragHitTest.IsInteractive(control),
                        $"{control.GetType().Name} 应被视为交互控件");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "global drag deny test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void RealPages_KeepDisplayTextDraggableAndControlsInteractive()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureThemeResources();
                var dashboard = new AIGeekTuner.Views.Dashboard();
                var settings = new AIGeekTuner.Views.SettingsPage();
                var sessions = new AIGeekTuner.Views.SessionsPage();
                var hardware = new AIGeekTuner.Views.HardwareInfoPage();

                foreach (var page in new Page[] { dashboard, settings, sessions, hardware })
                {
                    page.Measure(new Size(1024, 720));
                    page.Arrange(new Rect(0, 0, 1024, 720));
                    page.UpdateLayout();
                }

                var dashboardTitle = FindVisual<TextBlock>(dashboard,
                    block => block.Text == "仪表盘");
                var settingsTitle = FindVisual<TextBlock>(settings,
                    block => block.Text == "设置");
                var hardwareTitle = FindVisual<TextBlock>(hardware,
                    block => block.Text == "硬件信息");
                var settingsInput = FindVisual<TextBox>(settings, _ => true);
                var settingsCombo = FindVisual<ComboBox>(settings, _ => true);
                var sessionsList = FindVisual<ListBox>(sessions, _ => true);
                var sessionsCard = FindVisual<Border>(sessions, _ => true);

                Assert.NotNull(dashboardTitle);
                Assert.NotNull(settingsTitle);
                Assert.NotNull(hardwareTitle);
                Assert.NotNull(settingsInput);
                Assert.NotNull(settingsCombo);
                Assert.NotNull(sessionsList);
                Assert.NotNull(sessionsCard);
                Assert.True(WindowDragHitTest.IsDraggableFrom(dashboardTitle, dashboard));
                Assert.True(WindowDragHitTest.IsDraggableFrom(settingsTitle, settings));
                Assert.True(WindowDragHitTest.IsDraggableFrom(hardwareTitle, hardware));
                Assert.False(WindowDragHitTest.IsDraggableFrom(settingsInput, settings));
                Assert.False(WindowDragHitTest.IsDraggableFrom(settingsCombo, settings));
                Assert.False(WindowDragHitTest.IsDraggableFrom(sessionsList, sessions));
                Assert.True(WindowDragHitTest.IsDraggableFrom(sessionsCard, sessions));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "real page hit-tree test 超时");
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

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length)
        / value.Length;

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        if (root is T typed && predicate(typed))
        {
            return typed;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindVisual<T>(VisualTreeHelper.GetChild(root, index), predicate);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
