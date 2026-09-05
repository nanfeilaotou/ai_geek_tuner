using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Services.Navigation;

namespace AIGeekTuner.ViewModels
{
    public sealed class MainWindowViewModel : ViewModelBase
    {
        private readonly INavigationService _navigationService;
        private AppPage? _currentPage;

        public MainWindowViewModel(INavigationService navigationService)
        {
            ArgumentNullException.ThrowIfNull(navigationService);
            _navigationService = navigationService;

            if (navigationService is IPageNavigationNotifications notifications)
            {
                notifications.CurrentPageChanged += OnCurrentPageChanged;
            }

            ShowDashboardCommand = NavigateTo(AppPage.Dashboard);
            ShowHardwareCommand = NavigateTo(AppPage.Hardware);
            ShowDiagnosisCommand = NavigateTo(AppPage.Diagnosis);
            ShowSessionsCommand = NavigateTo(AppPage.Sessions);
            ShowResultCommand = NavigateTo(AppPage.Result);
            ShowHistoryCommand = NavigateTo(AppPage.History);
            ShowSettingsCommand = NavigateTo(AppPage.Settings);
        }

        public string Title => "AI-GeekTuner";

        public ICommand ShowDashboardCommand { get; }
        public ICommand ShowHardwareCommand { get; }
        public ICommand ShowDiagnosisCommand { get; }
        public ICommand ShowSessionsCommand { get; }
        public ICommand ShowResultCommand { get; }
        public ICommand ShowHistoryCommand { get; }
        public ICommand ShowSettingsCommand { get; }

        /// <summary>当前页面名称（与导航按钮 Tag 匹配，用于选中态样式）。</summary>
        public string CurrentPageName =>
            _currentPage?.ToString() ?? string.Empty;

        /// <summary>当前页面在 integrated window chrome 中显示的短标题。</summary>
        public string CurrentPageDisplayName =>
            _currentPage switch
            {
                AppPage.Dashboard => "仪表盘",
                AppPage.Hardware => "硬件信息",
                AppPage.Diagnosis => "AI 智能诊断",
                AppPage.Sessions => "数据录制",
                AppPage.Result => "诊断报告",
                AppPage.History => "日志管理",
                AppPage.Settings => "设置",
                _ => string.Empty
            };

        private ICommand NavigateTo(AppPage page) =>
            new RelayCommand(() =>
            {
                _currentPage = page;
                OnPropertyChanged(nameof(CurrentPageName));
                OnPropertyChanged(nameof(CurrentPageDisplayName));
                _navigationService.NavigateTo(page);
            });

        private void OnCurrentPageChanged(AppPage page)
        {
            if (_currentPage == page)
            {
                return;
            }

            _currentPage = page;
            OnPropertyChanged(nameof(CurrentPageName));
            OnPropertyChanged(nameof(CurrentPageDisplayName));
        }
    }
}
