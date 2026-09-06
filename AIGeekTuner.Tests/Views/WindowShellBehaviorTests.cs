using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AIGeekTuner;
using AIGeekTuner.Views.Behaviors;
using Xunit;

namespace AIGeekTuner.Tests.Views;

/// <summary>
/// M5.2C：窗口外壳行为契约 —— 最大化工作区、任务栏标准语义、固定
/// Normal 尺寸恢复、最大化状态不得引入外圈补偿边距。
/// 原生窗口管理器断言对跨测试窗口状态敏感，因此全部真实窗口操作
/// 收敛在一个 STA 测试内（单窗口单线程，按真实使用顺序推进）。
/// </summary>
[Collection("WpfSmoke")]
public sealed class WindowShellBehaviorTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    private const int GWLStyle = -16;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int ScRestore = 0xF120;

    // ---- 纯逻辑：WM_GETMINMAXINFO 工作区数学（覆盖多显示器 / 任务栏位置）----

    [Fact]
    public void MaximizeBounds_UseWorkAreaNotFullMonitor()
    {
        var mmi = new WindowMaximizeWorkArea.MinMaxInfo();
        var monitor = new WindowMaximizeWorkArea.NativeRect { Left = 0, Top = 0, Right = 2560, Bottom = 1600 };
        var work = new WindowMaximizeWorkArea.NativeRect { Left = 0, Top = 0, Right = 2560, Bottom = 1528 };

        WindowMaximizeWorkArea.FillFromMonitor(ref mmi, work, monitor);

        Assert.Equal(2560, mmi.PtMaxSize.X);
        Assert.Equal(1528, mmi.PtMaxSize.Y);
        Assert.Equal(0, mmi.PtMaxPosition.X);
        Assert.Equal(0, mmi.PtMaxPosition.Y);
    }

    [Fact]
    public void MaximizeBounds_AreMonitorRelativeForSecondaryMonitor()
    {
        var mmi = new WindowMaximizeWorkArea.MinMaxInfo();
        var monitor = new WindowMaximizeWorkArea.NativeRect { Left = 2560, Top = 0, Right = 4480, Bottom = 1400 };
        var work = new WindowMaximizeWorkArea.NativeRect { Left = 2560, Top = 0, Right = 4480, Bottom = 1340 };

        WindowMaximizeWorkArea.FillFromMonitor(ref mmi, work, monitor);

        Assert.Equal(1920, mmi.PtMaxSize.X);
        Assert.Equal(1340, mmi.PtMaxSize.Y);
        Assert.Equal(0, mmi.PtMaxPosition.X);
        Assert.Equal(0, mmi.PtMaxPosition.Y);
    }

    [Fact]
    public void MaximizeBounds_RespectSideDockedTaskbar()
    {
        var mmi = new WindowMaximizeWorkArea.MinMaxInfo();
        var monitor = new WindowMaximizeWorkArea.NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var work = new WindowMaximizeWorkArea.NativeRect { Left = 48, Top = 0, Right = 1920, Bottom = 1080 };

        WindowMaximizeWorkArea.FillFromMonitor(ref mmi, work, monitor);

        Assert.Equal(1872, mmi.PtMaxSize.X);
        Assert.Equal(1080, mmi.PtMaxSize.Y);
        Assert.Equal(48, mmi.PtMaxPosition.X);
        Assert.Equal(0, mmi.PtMaxPosition.Y);
    }

    // ---- 源码契约：最大化状态不得保留无效外圈补偿 ----

    [Fact]
    public void MaximizedState_KeepsNoOuterGapCompensation()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml")));
        var code = File.ReadAllText(FindRepositoryFile(Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml.cs")));
        var area = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "Views", "Behaviors", "WindowMaximizeWorkArea.cs")));

        // 根布局没有 margin/padding 补偿；最大化完全依赖 work area 语义。
        Assert.DoesNotContain("<Grid Margin", xaml);
        Assert.DoesNotContain("SystemParameters.WindowResizeBorderThickness", code);
        Assert.DoesNotContain("Thickness(-", code);
        // 使用 rcWork 而不是整屏 rcMonitor。
        Assert.Contains("RcWork", area);
    }

    // ---- 真窗口 STA：完整外壳契约（单窗口单线程顺序推进）----

    [Fact]
    public void WindowShell_MaximizeWorkArea_TaskbarAndRestoreContract()
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
                var work = GetWorkAreaRect(hwnd);

                // (6) 仍是适合任务栏的顶层窗口：未 owned、任务栏可见、
                // 且带 WS_MINIMIZEBOX / WS_MAXIMIZEBOX（shell 点击行为依据）。
                Assert.Null(window.Owner);
                Assert.True(window.ShowInTaskbar);
                var style = GetWindowLong(hwnd, GWLStyle);
                Assert.True((style & WsMinimizeBox) != 0, "WS_MINIMIZEBOX missing");
                Assert.True((style & WsMaximizeBox) != 0, "WS_MAXIMIZEBOX missing");

                // (2)(5) 最大化 = 精确填充当前显示器工作区：左右上边缘无白缝，
                // 底部不得越过工作区（任务栏遮挡回归锁）。
                WindowChromeController.ToggleMaximize(window);
                var rect = WaitForWindowRect(hwnd, r =>
                    r.Left == work.Left && r.Top == work.Top && r.Right == work.Right);
                Assert.Equal(work.Left, rect.Left);
                Assert.Equal(work.Top, rect.Top);
                Assert.Equal(work.Right, rect.Right);
                Assert.True(rect.Bottom <= work.Bottom,
                    $"maximized bottom {rect.Bottom} overlaps taskbar (work bottom {work.Bottom})");
                Assert.Equal(WindowState.Maximized, window.WindowState);

                // (7)(8) 任务栏点击 = shell 发送标准 SC_MINIMIZE / SC_RESTORE。
                SendMessage(hwnd, WmSysCommand, new IntPtr(ScMinimize), IntPtr.Zero);
                WaitForIconic(hwnd, expected: true);
                Assert.Equal(WindowState.Minimized, window.WindowState);

                SendMessage(hwnd, WmSysCommand, new IntPtr(ScRestore), IntPtr.Zero);
                WaitForIconic(hwnd, expected: false);
                Assert.Equal(WindowState.Maximized, window.WindowState);

                // (9) 自定义标题栏最小化按钮走同一标准路径。
                WindowChromeController.Minimize(window);
                WaitForIconic(hwnd, expected: true);
                Assert.Equal(WindowState.Minimized, window.WindowState);

                // 最小化前是 Maximized：原生 restore 语义 = 回到最小化前的
                // 最大化状态（SW_RESTORE），再经 toggle 回 Normal 固定尺寸。
                WindowChromeController.ToggleMaximize(window);
                WaitForIconic(hwnd, expected: false);
                Assert.Equal(WindowState.Maximized, window.WindowState);

                // (1)(3)(10) 还原回固定 1180x760；close/maximize 无回归。
                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(1180, window.Width);
                Assert.Equal(760, window.Height);

                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                WindowChromeController.ToggleMaximize(window);
                Assert.Equal(WindowState.Normal, window.WindowState);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "window shell contract test 超时");
        Assert.Null(failure);
    }

    private static RECT GetWorkAreaRect(IntPtr hwnd)
    {
        var hMonitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        Assert.NotEqual(IntPtr.Zero, hMonitor);
        var info = new MONITORINFO { CbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        Assert.True(GetMonitorInfoW(hMonitor, ref info));
        return info.RcWork;
    }

    private static RECT WaitForWindowRect(IntPtr hwnd, Func<RECT, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var rect = default(RECT);
        while (DateTime.UtcNow < deadline)
        {
            if (GetWindowRect(hwnd, out rect) && condition(rect))
            {
                return rect;
            }

            Thread.Sleep(60);
        }

        return rect;
    }

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