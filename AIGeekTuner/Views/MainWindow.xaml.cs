using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.Dialogs;
using AIGeekTuner.Services.Files;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.History;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.Services.Safety;
using AIGeekTuner.Services.Context;
using AIGeekTuner.Services.Knowledge;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Services.Reports;
using AIGeekTuner.Services.Storage;
using AIGeekTuner.ViewModels;
using AIGeekTuner.Views;

namespace AIGeekTuner
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient;
        private readonly FrameNavigationService _navigationService;
        private readonly IFilePickerService _filePickerService;
        private readonly IFileReaderService _fileReaderService;
        private readonly IConfirmationDialogService _confirmationDialogService;
        private readonly IReportExportService _reportExportService;
        private readonly IHardwareDetectionService _hardwareDetectionService;
        private readonly IHardwareSensorService _hardwareSensorService;
        private readonly IDiagnosisService _diagnosisService;
        private readonly IDiagnosisHistoryService _diagnosisHistoryService;
        private readonly ISystemContextCollector _systemContextCollector;
        private readonly IDiagnosticKnowledgeService _knowledgeService;
        private readonly IApplicationSettingsService _applicationSettingsService;
        private readonly ILocalDataDirectoryService _localDataDirectoryService;
        private readonly HardwareInfoViewModel _hardwareInfoViewModel;
        private readonly LatestDiagnosisState _latestDiagnosisState;
        private readonly SettingsViewModel _settingsViewModel;

        public MainWindow()
        {
            InitializeComponent();

            var applicationDataPaths = ApplicationDataPaths.Default;
            _httpClient = new HttpClient();
            _filePickerService = new OpenFileDialogService();
            _fileReaderService = new FileReaderService();
            _confirmationDialogService = new MessageBoxConfirmationDialogService();
            _reportExportService = new MarkdownReportExportService(
                applicationDataPaths);
            _hardwareDetectionService = new WmiHardwareDetectionService();
            _hardwareSensorService = new LibreHardwareMonitorSensorService();
            _hardwareInfoViewModel = new HardwareInfoViewModel(
                _hardwareDetectionService,
                _hardwareSensorService);
            _latestDiagnosisState = new LatestDiagnosisState();
            _diagnosisHistoryService = new LocalDiagnosisHistoryService(
                applicationDataPaths);
            _systemContextCollector = new SystemContextCollector();
            _knowledgeService = new DiagnosticKnowledgeService();
            _applicationSettingsService = new JsonApplicationSettingsService(
                applicationDataPaths);
            _localDataDirectoryService = new LocalDataDirectoryService(
                applicationDataPaths);

            var ollamaOptions = new OllamaOptions();
            var diagnosisInputOptions = new DiagnosisInputOptions();
            var aiService = new OllamaService(
                _httpClient,
                ollamaOptions,
                new DiagnosticResultParser());
            _settingsViewModel = new SettingsViewModel(
                ollamaOptions,
                diagnosisInputOptions,
                aiService,
                _applicationSettingsService,
                _localDataDirectoryService);
            var safetyService = new SafetyGuardService();
            _diagnosisService = new DiagnosisService(
                aiService,
                new DiagnosisPromptBuilder(diagnosisInputOptions),
                safetyService);

            _navigationService = new FrameNavigationService(MainFrame, CreatePage);
            DataContext = new MainWindowViewModel(_navigationService);
            _navigationService.NavigateTo(AppPage.Dashboard);
        }

        protected override void OnClosed(EventArgs e)
        {
            _httpClient.Dispose();
            base.OnClosed(e);
        }

        private Page CreatePage(AppPage page, object? parameter)
        {
            return page switch
            {
                AppPage.Dashboard => new Dashboard
                {
                    DataContext = new DashboardViewModel(_hardwareInfoViewModel)
                },
                AppPage.Hardware => new HardwareInfoPage
                {
                    DataContext = _hardwareInfoViewModel
                },
                AppPage.Diagnosis => new DiagnosisPage
                {
                    DataContext = new DiagnosisViewModel(
                        _navigationService,
                        _filePickerService,
                        _fileReaderService,
                        _hardwareDetectionService,
                        _diagnosisService,
                        _diagnosisHistoryService,
                        _systemContextCollector,
                        _knowledgeService,
                        _applicationSettingsService,
                        _latestDiagnosisState)
                },
                AppPage.Result => new ResultPage
                {
                    DataContext = new ResultViewModel(
                        _navigationService,
                        _reportExportService,
                        parameter as DiagnosisOutcome ?? _latestDiagnosisState.Outcome)
                },
                AppPage.History => new DiagnosisHistoryPage
                {
                    DataContext = new DiagnosisHistoryViewModel(
                        _navigationService,
                        _diagnosisHistoryService,
                        _confirmationDialogService,
                        _reportExportService,
                        _latestDiagnosisState)
                },
                AppPage.Settings => new SettingsPage
                {
                    DataContext = _settingsViewModel
                },
                _ => throw new ArgumentOutOfRangeException(nameof(page), page, null)
            };
        }
    }
}
