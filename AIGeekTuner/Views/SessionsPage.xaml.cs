using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    public partial class SessionsPage : Page
    {
        private readonly DispatcherTimer _uiTimer;

        public SessionsPage()
        {
            InitializeComponent();

            // UI 定时器只做“读取最新状态”这一件事（§45），采样在服务内部完成。
            _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _uiTimer.Tick += (_, _) =>
            {
                if (DataContext is SessionsViewModel viewModel && viewModel.IsRecording)
                {
                    viewModel.RefreshLive();
                }
            };
            Loaded += (_, _) =>
            {
                _uiTimer.Start();
                // V2-M4.5E Gate H：每次回到 Sessions 页应用入口规则
                //（录制中 → Record 视图；未录制 → 保留最近模式）。ViewModel 单例，
                // 页面实例可被重建，入口规则必须挂在页面上而不是构造器里。
                if (DataContext is SessionsViewModel enteredViewModel)
                {
                    enteredViewModel.OnPageEntered();
                }
            };
            Unloaded += (_, _) => _uiTimer.Stop();

            if (DataContext is SessionsViewModel vm)
            {
                vm.ConfirmDelete = id => MessageBox.Show(
                    Window.GetWindow(this),
                    "确定删除该录制会话？此操作不可恢复。",
                    "AI-GeekTuner",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK;
            }
        }
    }
}
