using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Services.Navigation;

namespace AIGeekTuner.ViewModels
{
    public sealed class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel(INavigationService navigationService)
        {
            ArgumentNullException.ThrowIfNull(navigationService);
            ShowDashboardCommand = Navigate(navigationService, AppPage.Dashboard);
            ShowHardwareCommand = Navigate(navigationService, AppPage.Hardware);
            ShowDiagnosisCommand = Navigate(navigationService, AppPage.Diagnosis);
            ShowResultCommand = Navigate(navigationService, AppPage.Result);
            ShowHistoryCommand = Navigate(navigationService, AppPage.History);
            ShowSettingsCommand = Navigate(navigationService, AppPage.Settings);
        }

        public string Title => "AI-GeekTuner";
        public ICommand ShowDashboardCommand { get; }
        public ICommand ShowHardwareCommand { get; }
        public ICommand ShowDiagnosisCommand { get; }
        public ICommand ShowResultCommand { get; }
        public ICommand ShowHistoryCommand { get; }
        public ICommand ShowSettingsCommand { get; }

        private static ICommand Navigate(INavigationService navigationService, AppPage page) =>
            new RelayCommand(() => navigationService.NavigateTo(page));
    }
}
