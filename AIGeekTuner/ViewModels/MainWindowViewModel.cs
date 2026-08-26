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

            ShowDashboardCommand = NavigateTo(AppPage.Dashboard);
            ShowHardwareCommand = NavigateTo(AppPage.Hardware);
            ShowDiagnosisCommand = NavigateTo(AppPage.Diagnosis);
            ShowResultCommand = NavigateTo(AppPage.Result);
            ShowHistoryCommand = NavigateTo(AppPage.History);
            ShowSettingsCommand = NavigateTo(AppPage.Settings);
        }

        public string Title => "AI-GeekTuner";

        public ICommand ShowDashboardCommand { get; }
        public ICommand ShowHardwareCommand { get; }
        public ICommand ShowDiagnosisCommand { get; }
        public ICommand ShowResultCommand { get; }
        public ICommand ShowHistoryCommand { get; }
        public ICommand ShowSettingsCommand { get; }

        /// <summary>当前页面名称（与导航按钮 Tag 匹配，用于选中态样式）。</summary>
        public string CurrentPageName =>
            _currentPage?.ToString() ?? string.Empty;

        private ICommand NavigateTo(AppPage page) =>
            new RelayCommand(() =>
            {
                _currentPage = page;
                OnPropertyChanged(nameof(CurrentPageName));
                _navigationService.NavigateTo(page);
            });
    }
}
