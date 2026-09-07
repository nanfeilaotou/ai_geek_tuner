using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AIGeekTuner;
using AIGeekTuner.ViewModels;
using AIGeekTuner.Views.Behaviors;
using Xunit;

namespace AIGeekTuner.Tests.Views;

/// <summary>
/// M5.2D.3：窗口 style 单一所有权契约。最小化能力由 WPF（ResizeMode）
/// 自持，禁止任何 HWND 创建后的手工 style patch；system menu 的
/// SC_MINIMIZE 必须在启动、导航、拖动、最小化/还原全程保持 enabled
///（否则任务栏前台点击最小化出现 split state 回归）。
/// 原生窗口管理断言对跨测试状态敏感，全部收敛在一个 STA 测试内。
/// </summary>
[Collection("WpfSmoke")]
public sealed class WindowStyleOwnershipTests
{
    private const int GWLStyle = -16;
    private const int GWLExStyle = -20;
    private const int WsCaption = 0x00C00000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const uint ScMinimize = 0xF020;
    private const uint MfByCommand = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;
    private const uint WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern int GetMenuState(IntPtr hMenu, uint uId, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

    // ---- 源码契约：没有 startup repaint hack，没有手工 style patch ----

    [Fact]
    public void ShellSource_HasNoFirstFrameRepaintWorkaroundAndNoManualStylePatch()
    {
        var mainCode = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "Views", "MainWindow.xaml.cs")));
        var controller = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "Views", "Behaviors", "WindowChromeController.cs")));
        var xaml = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "Views", "MainWindow.xaml")));

        // e84f8c5 的 first-frame workaround 已语义级移除。
        Assert.DoesNotContain("RunFirstChromeRefresh", mainCode);
        Assert.DoesNotContain("_firstChromeRefreshDone", mainCode);

        // 手工 SetWindowLong 补 MINBOX/MAXBOX 的 patch 已移除：
        // 最小化能力唯一 owner 是 WPF ResizeMode。
        Assert.DoesNotContain("ApplyStandardTaskbarBehavior", mainCode);
        Assert.DoesNotContain("ApplyStandardTaskbarBehavior", controller);
        Assert.DoesNotContain("SetWindowLong", controller);
        Assert.DoesNotContain("GetWindowLong", controller);

        // 单一 owner 声明。
        Assert.Contains("WindowStyle=\"SingleBorderWindow\"", xaml);
        Assert.Contains("ResizeMode=\"CanResize\"", xaml);
        Assert.Contains("CaptionHeight=\"40\"", xaml);
        Assert.Contains("ResizeBorderThickness=\"0\"", xaml);
        Assert.Contains("UseAeroCaptionButtons=\"False\"", xaml);

        // 自定义 caption 三键保持原样。
        Assert.Contains("x:Name=\"MinimizeButton\"", xaml);
        Assert.Contains("x:Name=\"MaximizeButton\"", xaml);
        Assert.Contains("x:Name=\"CloseButton\"", xaml);
    }

    // ---- 行为 STA：style bits + system menu 全程 invariant ----

    [Fact]
    public void WindowStyle_WpfOwnedBitsAndSystemMenu_InvariantAcrossLifecycle()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureThemeResources();
                var window = new MainWindow();
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;

                // 初始：WPF CanResize 自持标准 bits；无手工 patch 痕迹。
                AssertStyleBits(hwnd, "startup");
                AssertScMinimizeEnabled(hwnd, "startup");

                // navigation ×21：system menu 与 style bits 不漂移。
                var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                var commands = new[]
                {
                    viewModel.ShowHardwareCommand,
                    viewModel.ShowDiagnosisCommand,
                    viewModel.ShowSessionsCommand,
                    viewModel.ShowResultCommand,
                    viewModel.ShowHistoryCommand,
                    viewModel.ShowSettingsCommand,
                    viewModel.ShowDashboardCommand
                };
                for (var round = 0; round < 3; round++)
                {
                    foreach (var command in commands)
                    {
                        command.Execute(null);
                        window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                        AssertStyleBits(hwnd, $"navigation r{round}");
                        AssertScMinimizeEnabled(hwnd, $"navigation r{round}");
                    }
                }

                // 无 resize hit 区域：四边/四角 hit test 不得返回 size 边。
                AssertNoResizeHitRegions(window, hwnd);

                // 拖动等价扰动 ×20（WM_WINDOWPOSCHANGED churn）后不变。
                for (var i = 0; i < 20; i++)
                {
                    var dx = i % 2 == 0 ? 6 : -6;
                    NativeSetWindowPos(hwnd, dx, 0);
                    window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                    AssertStyleBits(hwnd, $"move {i}");
                    AssertScMinimizeEnabled(hwnd, $"move {i}");
                }

                // 自定义 maximize / restore（不依赖手工 MAXBOX）。
                WindowChromeController.ToggleMaximize(window);
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                Assert.Equal(WindowState.Maximized, window.WindowState);
                AssertStyleBits(hwnd, "maximized");
                AssertScMinimizeEnabled(hwnd, "maximized");
                WindowChromeController.ToggleMaximize(window);
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(1180, window.Width);
                Assert.Equal(760, window.Height);

                // minimize / restore ×20：SC_MINIMIZE 始终 enabled。
                for (var i = 0; i < 20; i++)
                {
                    WindowChromeController.Minimize(window);
                    WaitForIconic(hwnd, expected: true);
                    SendMessage(hwnd, 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF120 /* SC_RESTORE */), IntPtr.Zero);
                    WaitForIconic(hwnd, expected: false);
                    AssertStyleBits(hwnd, $"min/restore {i}");
                    AssertScMinimizeEnabled(hwnd, $"min/restore {i}");
                }

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "window style ownership test 超时");
        Assert.Null(failure);
    }

    private static void AssertStyleBits(IntPtr hwnd, string phase)
    {
        var style = GetWindowLong(hwnd, GWLStyle);
        Assert.True((style & WsMinimizeBox) != 0, $"{phase}: WS_MINIMIZEBOX missing");
        Assert.True((style & WsMaximizeBox) != 0, $"{phase}: WS_MAXIMIZEBOX missing");
        Assert.True((style & WsCaption) != 0, $"{phase}: WS_CAPTION missing (SingleBorderWindow)");
        var exStyle = GetWindowLong(hwnd, GWLExStyle);
        Assert.True((exStyle & WsExAppWindow) != 0, $"{phase}: WS_EX_APPWINDOW missing");
        Assert.True((exStyle & WsExToolWindow) == 0, $"{phase}: WS_EX_TOOLWINDOW present");
    }

    private static void AssertScMinimizeEnabled(IntPtr hwnd, string phase)
    {
        var menu = GetSystemMenu(hwnd, false);
        Assert.NotEqual(IntPtr.Zero, menu);
        var state = GetMenuState(menu, ScMinimize, MfByCommand);
        Assert.True(state >= 0, $"{phase}: SC_MINIMIZE menu item missing");
        Assert.True((state & (MfGrayed | MfDisabled)) == 0,
            $"{phase}: SC_MINIMIZE disabled in system menu (split state) state={state}");
    }

    private static void AssertNoResizeHitRegions(Window window, IntPtr hwnd)
    {
        var root = Assert.IsType<System.Windows.Controls.Grid>(window.Content);
        var midY = root.ActualHeight / 2;
        var edgeCases = new[]
        {
            new Point(0.5, midY),
            new Point(root.ActualWidth - 0.5, midY),
            new Point(root.ActualWidth / 2, 0.5),
            new Point(root.ActualWidth / 2, root.ActualHeight - 0.5),
            new Point(0.5, 0.5),
            new Point(root.ActualWidth - 0.5, 0.5),
            new Point(0.5, root.ActualHeight - 0.5),
            new Point(root.ActualWidth - 0.5, root.ActualHeight - 0.5)
        };
        foreach (var point in edgeCases)
        {
            var screen = root.PointToScreen(point);
            var lParam = new IntPtr((((int)screen.Y & 0xFFFF) << 16) | ((int)screen.X & 0xFFFF));
            var hit = SendMessage(hwnd, unchecked((int)WmNcHitTest), IntPtr.Zero, lParam).ToInt32();
            Assert.True(hit != HtLeft && hit != HtRight && hit != HtTop
                && hit != HtBottom && hit != HtTopLeft && hit != HtTopRight
                && hit != HtBottomLeft && hit != HtBottomRight,
                $"edge hit-test returned resize region {hit} at {point}");
            Assert.True(hit == HtClient || hit == HtCaption,
                $"edge hit-test returned unexpected region {hit} at {point}");
        }
    }

    private static void NativeSetWindowPos(IntPtr hwnd, int dx, int dy)
    {
        SetWindowPos(hwnd, IntPtr.Zero, dx, dy, 0, 0, 0x0005 /* NOSIZE | NOZORDER */ | 0x0010 /* NOACTIVATE */);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private static void WaitForIconic(IntPtr hwnd, bool expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && IsIconic(hwnd) != expected)
        {
            Thread.Sleep(60);
        }

        Assert.Equal(expected, IsIconic(hwnd));
    }

    private static void EnsureThemeResources()
    {
        var app = Application.Current ?? new Application();
        if (app.Resources.MergedDictionaries.Any(d =>
                d.Source?.OriginalString.Contains("BlueToolboxTheme", StringComparison.OrdinalIgnoreCase) == true))
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