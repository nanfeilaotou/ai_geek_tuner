using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using AIGeekTuner;
using Xunit;

namespace AIGeekTuner.Tests.Views;

/// <summary>
/// M5.2D.1：WindowChrome 首帧渲染修复契约 —— startup one-shot chrome
/// 刷新只在 HWND 就绪后、首份内容渲染完成时执行一次，失败不影响启动；
/// SingleBorderWindow / 原生动画语义 / 自定义 caption 按钮不得回退。
/// </summary>
[Collection("WpfSmoke")]
public sealed class WindowChromeFirstFrameTests
{
    // ---- 源码契约：one-shot 守卫 + 非致命刷新 ----

    [Fact]
    public void FirstChromeRefresh_IsOneShotAndNonFatalByContract()
    {
        var code = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "Views", "MainWindow.xaml.cs")));

        // one-shot：执行一次后取消 ContentRendered 订阅，禁止反复刷新。
        Assert.Contains("ContentRendered -= Window_ContentRendered", code);
        Assert.Contains("_firstChromeRefreshDone", code);

        // 轻路径刷新（Gate C 最小实现）。
        Assert.Contains("InvalidateVisual()", code);
        Assert.Contains("UpdateLayout()", code);

        // 失败非致命：try/catch 记录，不向启动路径抛出。
        Assert.Contains("catch (Exception exception)", code);
        Assert.Contains("\"First chrome refresh\"", code);
    }

    [Fact]
    public void WindowShell_ContractSurvivesFirstFrameFix()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(Path.Combine(
            "AIGeekTuner", "Views", "MainWindow.xaml")));

        // (7) SingleBorderWindow 保留——native minimize/restore 动画依赖标准
        // HWND frame 语义，禁止为消除首帧残影改回 None。
        Assert.Contains("WindowStyle=\"SingleBorderWindow\"", xaml);
        Assert.DoesNotContain("WindowStyle=\"None\"", xaml);

        // (9) 原生 aero caption 按钮仍被 WindowChrome 关闭。
        Assert.Contains("UseAeroCaptionButtons=\"False\"", xaml);

        // 首帧刷新挂在 ContentRendered（Gate E 时序），不在构造/SourceInitialized。
        Assert.Contains("ContentRendered=\"Window_ContentRendered\"", xaml);

        // (10) 自定义 caption 三键保持原样。
        Assert.Contains("x:Name=\"MinimizeButton\"", xaml);
        Assert.Contains("x:Name=\"MaximizeButton\"", xaml);
        Assert.Contains("x:Name=\"CloseButton\"", xaml);
    }

    // ---- 行为：HWND 门控 + 恰好一次 ----

    [Fact]
    public void FirstChromeRefresh_WaitsForHwndAndRunsExactlyOnce()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureThemeResources();
                var window = new MainWindow();

                // (2) HWND 未就绪（未 Show）时不得执行，也不得消费 one-shot。
                Assert.Equal(IntPtr.Zero,
                    new System.Windows.Interop.WindowInteropHelper(window).Handle);
                window.RunFirstChromeRefresh();
                Assert.False(window.FirstChromeRefreshCompleted);

                window.Show();
                window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                Assert.NotEqual(IntPtr.Zero,
                    new System.Windows.Interop.WindowInteropHelper(window).Handle);

                // (3) HWND 就绪后触发刷新，标记完成。
                window.RunFirstChromeRefresh();
                Assert.True(window.FirstChromeRefreshCompleted);

                // (1) 重复调用是 no-op：startup one-shot 语义。
                window.RunFirstChromeRefresh();
                window.RunFirstChromeRefresh();
                Assert.True(window.FirstChromeRefreshCompleted);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "first chrome refresh test 超时");
        Assert.Null(failure);
    }

    private static void EnsureThemeResources()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application();
        if (app.Resources.MergedDictionaries.Any(d =>
                d.Source?.OriginalString.Contains("BlueToolboxTheme", StringComparison.OrdinalIgnoreCase) == true))
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/AIGeekTuner;component/Themes/BlueToolboxTheme.xaml",
                UriKind.Absolute)
        });
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var candidate = AppContext.BaseDirectory;
        for (var depth = 0; depth < 6; depth++)
        {
            var probe = Path.Combine(candidate, relativePath);
            if (File.Exists(probe))
            {
                return probe;
            }

            candidate = Path.GetDirectoryName(candidate)!;
        }

        throw new FileNotFoundException($"无法定位测试文件 {relativePath}");
    }
}