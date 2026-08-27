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
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Aida64;
using AIGeekTuner.Services.Telemetry.HwInfo;
using AIGeekTuner.Services.Telemetry.LibreHardwareMonitor;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Voice;
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
        private readonly ITelemetryHub _telemetryHub;
        private readonly ITelemetryRecordingService _recordingService;
        private readonly SessionsViewModel _sessionsViewModel;
        private readonly LiveTelemetryCoordinator _liveTelemetryCoordinator;
        private readonly IDiagnosisService _diagnosisService;
        private readonly IDiagnosisHistoryService _diagnosisHistoryService;
        private readonly ISystemContextCollector _systemContextCollector;
        private readonly IDiagnosticKnowledgeService _knowledgeService;
        private readonly IApplicationSettingsService _applicationSettingsService;
        private readonly ILocalDataDirectoryService _localDataDirectoryService;
        private readonly IOllamaConnectionService _connectionService;
        private readonly HardwareInfoViewModel _hardwareInfoViewModel;
        private readonly LatestDiagnosisState _latestDiagnosisState;
        private readonly SettingsViewModel _settingsViewModel;

        /// <summary>运行时配置中心；设置页保存后整体换入新快照。</summary>
        internal DiagnosticConfigurationStore ConfigurationStore { get; }

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

            // V2-M1 统一遥测层：固定优先级 HWiNFO → AIDA64 → LibreHardwareMonitor。
            _telemetryHub = new TelemetryHub(new ITelemetryProvider[]
            {
                new HwInfoTelemetryProvider(new HwInfoSharedMemoryReader()),
                new Aida64TelemetryProvider(new Aida64WmiSensorReader()),
                new LibreHardwareMonitorTelemetryProvider()
            });

            // V2-M2：Recorder 单实例服务——生命周期独立于页面（§33）。
            var sessionStore = new TelemetrySessionStore(applicationDataPaths);
            _recordingService = new TelemetryRecordingService(_telemetryHub);
            var liveTelemetry = new LiveTelemetryCoordinator(_telemetryHub, _recordingService);

            // V2-M3：Session AI（复用现有 HttpClient 与配置快照原则）+ GPT-SoVITS。
            var analysisChatClient = new OllamaChatClient(
                _httpClient,
                () => ConfigurationStore.Snapshot().Ollama);
            var analysisService = new OllamaSessionAnalysisService(analysisChatClient, new SessionAnalysisPromptBuilder());
            var analysisStore = new SessionAnalysisStore(applicationDataPaths.SessionsDirectory);
            var voiceHttpClient = new HttpClient();
            var voiceService = new GptSoVitsVoiceSynthesisService(voiceHttpClient);
            var wavPlayback = new SoundPlayerWavPlaybackService();
            Func<VoiceConfiguration> voiceSnapshot = () =>
            {
                var voice = _applicationSettingsService.Current.Voice;
                return new VoiceConfiguration(
                    voice.Endpoint,
                    voice.ReferenceAudioPath,
                    voice.PromptText,
                    voice.PromptLang,
                    "zh",
                    voice.SpeedFactor,
                    string.IsNullOrWhiteSpace(voice.GptModelPath) ? null : voice.GptModelPath,
                    string.IsNullOrWhiteSpace(voice.SovitsModelPath) ? null : voice.SovitsModelPath);
            };

            _hardwareInfoViewModel = new HardwareInfoViewModel(
                _hardwareDetectionService,
                _hardwareSensorService,
                _telemetryHub);
            _latestDiagnosisState = new LatestDiagnosisState();
            _diagnosisHistoryService = new LocalDiagnosisHistoryService(
                applicationDataPaths);
            _systemContextCollector = new SystemContextCollector();
            _knowledgeService = new DiagnosticKnowledgeService();
            _applicationSettingsService = new JsonApplicationSettingsService(
                applicationDataPaths);
            _localDataDirectoryService = new LocalDataDirectoryService(
                applicationDataPaths);

            // 运行时配置中心：设置页保存后 Replace 新快照，
            // 下一次诊断立即生效；进行中的诊断持有旧快照不受影响。
            ConfigurationStore = CreateConfigurationStore(_applicationSettingsService.Current);

            var aiService = new OllamaService(
                _httpClient,
                () => ConfigurationStore.Snapshot().Ollama,
                new DiagnosticResultParser());
            _connectionService = new OllamaConnectionService(_httpClient);
            _settingsViewModel = new SettingsViewModel(
                _applicationSettingsService,
                _connectionService,
                ConfigurationStore,
                _localDataDirectoryService,
                _telemetryHub);
            _liveTelemetryCoordinator = liveTelemetry;
            _sessionsViewModel = new SessionsViewModel(
                _recordingService,
                sessionStore,
                _applicationSettingsService,
                analysisService,
                analysisStore,
                voiceService,
                wavPlayback,
                voiceSnapshot);
            var safetyService = new SafetyGuardService();
            _diagnosisService = new DiagnosisService(
                aiService,
                new OllamaAiReadinessService(_connectionService),
                new DiagnosisPromptBuilder(() => ConfigurationStore.Snapshot().Input),
                safetyService);

            _navigationService = new FrameNavigationService(MainFrame, CreatePage);
            DataContext = new MainWindowViewModel(_navigationService);
            _navigationService.NavigateTo(AppPage.Dashboard);
        }

        private static DiagnosticConfigurationStore CreateConfigurationStore(
            ApplicationSettings settings)
        {
            return new DiagnosticConfigurationStore(
                new DiagnosticConfiguration(
                    new OllamaOptions
                    {
                        BaseUrl = settings.OllamaBaseUrl,
                        ModelName = settings.OllamaModelName,
                        TimeoutSeconds = settings.OllamaTimeoutSeconds,
                        UseJsonFormat = settings.UseJsonFormat
                    },
                    new DiagnosisInputOptions
                    {
                        MaxFaultLogCharacters = settings.MaxFaultLogCharacters
                    }));
        }

        protected override void OnClosed(EventArgs e)
        {
            // 录制中的会话 best-effort 收尾（§34）：不阻塞退出超过 5 秒。
            try
            {
                _recordingService.FinalizeIfRecordingAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(exception, "Recorder shutdown finalize");
            }

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
                        ConfigurationStore,
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
                AppPage.Sessions => new SessionsPage
                {
                    DataContext = _sessionsViewModel
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


