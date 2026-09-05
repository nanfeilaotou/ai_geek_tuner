using System.Collections.ObjectModel;
using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Services.Telemetry;

namespace AIGeekTuner.ViewModels
{
    public enum SettingsStatusKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// 设置页视图模型：编辑的是“草稿”，保存成功（校验通过且磁盘写完成）
    /// 后才原子换入运行时配置快照——下一次诊断立即生效。
    /// </summary>
    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly IApplicationSettingsService _settingsService;
        private readonly DiagnosticConfigurationStore _configurationStore;
        private readonly ILocalDataDirectoryService _localDataDirectoryService;
        private readonly ITelemetryHub? _telemetryHub;

        private readonly AsyncRelayCommand _refreshDataSourcesCommand;

        private bool _isRefreshingDataSources;

        private string _dataSourceStatusLine = "正在检测硬件数据源...";

        private readonly AsyncRelayCommand _saveCommand;

        // V2-M5.1B（Gate N）：旧 Ollama 专属的 BaseUrl / 模型 / 刷新模型 / 测试连接
        // 已从用户可见 UI 移除——由“AI 服务提供方”卡片的 Provider 配置取代。
        // 旧持久化字段（OllamaBaseUrl / OllamaModelName / UseJsonFormat）保留兼容，保存时原样带回。
        private string _timeoutSecondsText;
        private string _maxLogLengthText;
        private bool _autoSaveDiagnosisHistory;

        private bool _isSaving;

        private string _statusMessage = "更改完成后点击“保存设置”。连接检测与保存互不影响。";
        private SettingsStatusKind _statusKind = SettingsStatusKind.Info;

        public SettingsViewModel(
            IApplicationSettingsService settingsService,
            DiagnosticConfigurationStore configurationStore,
            ILocalDataDirectoryService localDataDirectoryService,
            ITelemetryHub? telemetryHub = null,
            AiProviderSettingsViewModel? providers = null)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
            _localDataDirectoryService = localDataDirectoryService ?? throw new ArgumentNullException(nameof(localDataDirectoryService));
            _telemetryHub = telemetryHub;

            // V2-M5.1A：Provider 配置卡片（“当前使用”Provider 决定真实 AI runtime）。
            Providers = providers ?? AiProviderSettingsViewModel.CreateUnavailable();

            var current = settingsService.Current;
            _timeoutSecondsText = current.OllamaTimeoutSeconds.ToString();
            _maxLogLengthText = current.MaxFaultLogCharacters.ToString("N0");
            _autoSaveDiagnosisHistory = current.AutoSaveDiagnosisHistory;
            RecordingIntervalMs = current.RecordingIntervalMs;
            VoiceEnabled = current.Voice.Enabled;
            VoiceEndpoint = current.Voice.Endpoint;
            VoiceReferenceAudioPath = current.Voice.ReferenceAudioPath;
            VoicePromptText = current.Voice.PromptText;
            VoicePromptLang = current.Voice.PromptLang;
            VoiceSpeedFactorText = current.Voice.SpeedFactor.ToString(
                "0.0##", System.Globalization.CultureInfo.InvariantCulture);

            _saveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
            OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
            _refreshDataSourcesCommand = new AsyncRelayCommand(
                RefreshDataSourceStatusesAsync,
                () => !IsRefreshingDataSources);

            // 打开设置页时自动检测一次；hub 未注入（旧测试/无遥测场景）时静默跳过。
            if (_telemetryHub is not null)
            {
                _ = RefreshDataSourceStatusesAsync();
            }
        }

        /// <summary>硬件数据源状态（V2-M1：只读展示 + 刷新检测，无可配置项）。</summary>
        public ObservableCollection<TelemetrySourceStatusViewModel> DataSourceStatuses { get; } = [];

        /// <summary>V2-M5.1A：AI 服务提供方卡片（Provider 配置，独立于旧 Ollama runtime）。</summary>
        public AiProviderSettingsViewModel Providers { get; }

        public ICommand RefreshDataSourcesCommand => _refreshDataSourcesCommand;

        public string DataSourceStatusLine
        {
            get => _dataSourceStatusLine;
            private set => SetProperty(ref _dataSourceStatusLine, value);
        }

        public bool IsRefreshingDataSources
        {
            get => _isRefreshingDataSources;
            private set
            {
                if (SetProperty(ref _isRefreshingDataSources, value))
                {
                    _refreshDataSourcesCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(RefreshDataSourceButtonText));
                }
            }
        }

        public string RefreshDataSourceButtonText =>
            IsRefreshingDataSources ? "检测中..." : "刷新检测";

        private async Task RefreshDataSourceStatusesAsync()
        {
            IsRefreshingDataSources = true;
            try
            {
                var hub = _telemetryHub;
                if (hub is null)
                {
                    DataSourceStatusLine = "遥测聚合未启用。";
                    return;
                }

                var snapshot = await hub.ReadAsync();
                DataSourceStatuses.Clear();
                foreach (var report in snapshot.Sources)
                {
                    DataSourceStatuses.Add(new TelemetrySourceStatusViewModel(report));
                }

                var readyCount = snapshot.Sources.Count(report =>
                    report.Status == Models.Telemetry.TelemetrySourceStatus.Ready);
                DataSourceStatusLine = $"检测完成 · {readyCount}/{snapshot.Sources.Count} 个来源就绪。";
            }
            catch
            {
                DataSourceStatusLine = "检测失败，请重试。";
            }
            finally
            {
                IsRefreshingDataSources = false;
            }
        }

        public string TimeoutSecondsText
        {
            get => _timeoutSecondsText;
            set => SetProperty(ref _timeoutSecondsText, value);
        }

        public string MaxLogLengthText
        {
            get => _maxLogLengthText;
            set => SetProperty(ref _maxLogLengthText, value);
        }

        public bool AutoSaveDiagnosisHistory
        {
            get => _autoSaveDiagnosisHistory;
            set => SetProperty(ref _autoSaveDiagnosisHistory, value);
        }

        /// <summary>诊断录制采样间隔（毫秒）；合法值 1000/2000/5000。</summary>
        public int RecordingIntervalMs { get; set; }

        public bool VoiceEnabled { get; set; }
        public string VoiceEndpoint { get; set; } = string.Empty;
        public string VoiceReferenceAudioPath { get; set; } = string.Empty;
        public string VoicePromptText { get; set; } = string.Empty;

        /// <summary>V2-M3.1：参考音频语言（zh / ja / en，范围由 Validator 权威定义）。</summary>
        public string VoicePromptLang { get; set; } = "ja";

        /// <summary>语速文本（0.7–1.3），保存时统一解析与校验。</summary>
        public string VoiceSpeedFactorText { get; set; } = "1.0";

        public string LocalDataDirectory => _localDataDirectoryService.DirectoryPath;

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (SetProperty(ref _isSaving, value))
                {
                    _saveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public SettingsStatusKind StatusKind
        {
            get => _statusKind;
            private set => SetProperty(ref _statusKind, value);
        }

        public AsyncRelayCommand SaveCommand => _saveCommand;

        public RelayCommand OpenDataDirectoryCommand { get; }

        private async Task SaveAsync()
        {
            if (!TryBuildSettingsFromDrafts(out var settings, out var validationMessage))
            {
                SetStatus(SettingsStatusKind.Warning, validationMessage!);
                return;
            }

            IsSaving = true;
            try
            {
                // 先写盘（服务内部再次权威校验），成功后才换入运行时快照。
                // V2-M5.1B（Gate N）：旧 Ollama 字段不再来自 UI——保存时原样带回旧值，保持升级兼容。
                await _settingsService.SaveAsync(settings);
                _configurationStore.Replace(new DiagnosticConfiguration(
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

                SetStatus(SettingsStatusKind.Success, "设置已保存，下一次诊断立即生效。");
            }
            catch (ApplicationSettingsException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SettingsViewModel.SaveAsync");
                SetStatus(SettingsStatusKind.Error, "设置保存失败，请稍后重试。");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private void OpenDataDirectory()
        {
            try
            {
                _localDataDirectoryService.Open();
                SetStatus(SettingsStatusKind.Success, "已打开本地数据目录。");
            }
            catch (LocalDataDirectoryException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SettingsViewModel.OpenDataDirectory");
                SetStatus(SettingsStatusKind.Error, "无法打开本地数据目录，请稍后重试。");
            }
        }

        private bool TryBuildSettingsFromDrafts(
            out ApplicationSettings settings,
            out string? validationMessage)
        {
            settings = null!;
            validationMessage = null;

            var previous = _settingsService.Current;
            var parsed = TryParseInt(TimeoutSecondsText, "请求超时", ref validationMessage)
                && TryParseInt(MaxLogLengthText, "诊断输入长度", ref validationMessage, formatted: true);
            if (!parsed || validationMessage is not null)
            {
                return false;
            }

            // V2-M3.1：语速解析（不变文化优先，兼容本机区域小数点）。
            if (!TryParseSpeed(VoiceSpeedFactorText, out var speedFactor))
            {
                validationMessage = "语音速度必须是有效的数字。";
                return false;
            }

            settings = new ApplicationSettings
            {
                AutoSaveDiagnosisHistory = AutoSaveDiagnosisHistory,

                // V2-M5.1B（Gate N）：旧 Ollama 字段原样保留（数据兼容）；runtime 不再从这些字段选择模型。
                OllamaBaseUrl = previous.OllamaBaseUrl,
                OllamaModelName = previous.OllamaModelName,
                OllamaTimeoutSeconds = int.Parse(TimeoutSecondsText),
                MaxFaultLogCharacters = int.Parse(MaxLogLengthText, System.Globalization.NumberStyles.AllowThousands),
                UseJsonFormat = previous.UseJsonFormat,
                RecordingIntervalMs = RecordingIntervalMs,
                Voice = new VoiceSettings
                {
                    Enabled = VoiceEnabled,
                    Endpoint = VoiceEndpoint.Trim(),
                    ReferenceAudioPath = VoiceReferenceAudioPath.Trim(),
                    PromptText = VoicePromptText,
                    PromptLang = VoicePromptLang.Trim(),
                    SpeedFactor = speedFactor,
                    GptModelPath = previous.Voice.GptModelPath,
                    SovitsModelPath = previous.Voice.SovitsModelPath,
                }
            };

            var errors = ApplicationSettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                validationMessage = string.Join(" ", errors);
                return false;
            }

            return true;

            static bool TryParseInt(
                string text,
                string label,
                ref string? validationMessage,
                bool formatted = false)
            {
                if (int.TryParse(
                        text,
                        System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.CurrentCulture,
                        out _))
                {
                    return true;
                }

                validationMessage = $"{label}必须是有效的整数。";
                return false;
            }
        }

        // V2-M3.1：语速解析（不变文化优先，兼容本机区域小数点）。
        static bool TryParseSpeed(string text, out double speedFactor)
        {
            if (double.TryParse(
                    text.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out speedFactor))
            {
                return true;
            }

            return double.TryParse(
                text.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out speedFactor);
        }

        private void SetStatus(SettingsStatusKind kind, string message)
        {
            StatusKind = kind;
            StatusMessage = message;
        }
    }
}
