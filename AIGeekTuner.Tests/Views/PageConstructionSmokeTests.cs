using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
