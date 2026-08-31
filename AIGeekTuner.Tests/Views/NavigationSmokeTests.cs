using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.Views
{
    /// <summary>
    /// V2-M3.3 §16 导航回归：Sessions 命令路由唯一；
    /// MainWindow.xaml 中“数据录制”按钮必须只有一个（防止再次出现双入口）。
    /// </summary>
    public sealed class NavigationSmokeTests
    {
        private sealed class FakeNavigationService : INavigationService
        {
            public bool CanGoBack => false;

            public bool IsCurrent(AppPage page) => false;

            public List<(AppPage Page, object? Parameter)> Calls { get; } = [];

            public void NavigateTo(AppPage page, object? parameter = null) =>
                Calls.Add((page, parameter));

            public void GoBack()
            {
            }
        }

        [Fact]
        public void SessionsCommand_NavigatesToSessions()
        {
            var navigation = new FakeNavigationService();
            var viewModel = new MainWindowViewModel(navigation);

            ((ICommand)viewModel.ShowSessionsCommand).Execute(null);

            var call = Assert.Single(navigation.Calls);
            Assert.Equal(AppPage.Sessions, call.Page);
        }

        [Fact]
        public void MainWindowXaml_ContainsExactlyOneSessionsNavButton()
        {
            var xamlPath = FindRepositoryFile(
                Path.Combine("AIGeekTuner", "Views", "MainWindow.xaml"));
            var xaml = File.ReadAllText(xamlPath);

            Assert.Single(Regex.Matches(xaml, "数据录制"));
        }

        private static string FindRepositoryFile(string relativePath)
        {
            // 测试输出目录：&lt;repo&gt;/AIGeekTuner.Tests/bin/&lt;cfg&gt;/&lt;tfm&gt; → 上溯 4 层到仓库根。
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

            throw new FileNotFoundException(
                $"无法在仓库中定位 {relativePath}（导航 smoke 测试依赖源码布局）。");
        }
    }
}

