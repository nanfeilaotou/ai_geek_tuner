using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Dialogs;
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
        private readonly IFileDialogService? _fileDialogs;
        private readonly IConfirmationDialogService? _confirmationDialog;
        private readonly ApplicationSettingsPortabilityService? _settingsPortability;
        private readonly AiProviderConfigurationPortabilityService? _providerPortability;

        private readonly AsyncRelayCommand _refreshDataSourcesCommand;

        private bool _isRefreshingDataSources;

        private string _dataSourceStatusLine = "正在检测硬件数据源...";

        private readonly AsyncRelayCommand _exportSettingsCommand;
        private readonly AsyncRelayCommand _importSettingsCommand;
        private readonly AsyncRelayCommand _exportProvidersCommand;
        private readonly AsyncRelayCommand _importProvidersCommand;

        // V2-M5.1B（Gate N）：旧 Ollama 专属的 BaseUrl / 模型 / 刷新模型 / 测试连接
        // 已从用户可见 UI 移除——由“AI 服务提供方”卡片的 Provider 配置取代。
        // 旧持久化字段（OllamaBaseUrl / OllamaModelName / UseJsonFormat）保留兼容，保存时原样带回；
        // ApplicationSettingsPortabilityService 的 v2 DTO 不再导出这些字段。
        private string _timeoutSecondsText;
        private string _maxLogLengthText;
        private bool _autoSaveDiagnosisHistory;
        private int _recordingIntervalMs;
        private bool _voiceEnabled;
        private string _voiceEndpoint = string.Empty;
        private string _voiceReferenceAudioPath = string.Empty;
        private string _voicePromptText = string.Empty;
        private string _voicePromptLang = "ja";
        private string _voiceSpeedFactorText = "1.0";

        private bool _isSaving;

        private string _statusMessage = "更改会自动保存并立即生效；AI Provider 配置单独保存。";
        private SettingsStatusKind _statusKind = SettingsStatusKind.Info;

        public SettingsViewModel(
            IApplicationSettingsService settingsService,
            DiagnosticConfigurationStore configurationStore,
            ILocalDataDirectoryService localDataDirectoryService,
            ITelemetryHub? telemetryHub = null,
            AiProviderSettingsViewModel? providers = null,
            IFileDialogService? fileDialogs = null,
            ApplicationSettingsPortabilityService? settingsPortability = null,
            AiProviderConfigurationPortabilityService? providerPortability = null,
            IConfirmationDialogService? confirmationDialog = null)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
            _localDataDirectoryService = localDataDirectoryService ?? throw new ArgumentNullException(nameof(localDataDirectoryService));
            _telemetryHub = telemetryHub;
            _fileDialogs = fileDialogs;
            _confirmationDialog = confirmationDialog;
            _settingsPortability = settingsPortability;
            _providerPortability = providerPortability;

            // V2-M5.1A：Provider 配置卡片（“当前使用”Provider 决定真实 AI runtime）。
            Providers = providers ?? AiProviderSettingsViewModel.CreateUnavailable();

            var current = settingsService.Current;
            // 构造期草稿回填不是用户变更：不置脏，不触发启动自动保存。
            _suppressAutoSave = true;
            try
            {
                _timeoutSecondsText = current.OllamaTimeoutSeconds.ToString();
                _maxLogLengthText = current.MaxFaultLogCharacters.ToString("N0");
                _autoSaveDiagnosisHistory = current.AutoSaveDiagnosisHistory;
                _recordingIntervalMs = current.RecordingIntervalMs;
                _voiceEnabled = current.Voice.Enabled;
                _voiceEndpoint = current.Voice.Endpoint;
                _voiceReferenceAudioPath = current.Voice.ReferenceAudioPath;
                _voicePromptText = current.Voice.PromptText;
                _voicePromptLang = current.Voice.PromptLang;
                _voiceSpeedFactorText = current.Voice.SpeedFactor.ToString(
                    "0.0##", System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                _suppressAutoSave = false;
            }

            _exportSettingsCommand = new AsyncRelayCommand(ExportSettingsAsync, () => !IsSaving);
            _importSettingsCommand = new AsyncRelayCommand(ImportSettingsAsync, () => !IsSaving);
            _exportProvidersCommand = new AsyncRelayCommand(ExportProvidersAsync, () => !IsSaving);
            _importProvidersCommand = new AsyncRelayCommand(ImportProvidersAsync, () => !IsSaving);
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

        // ---- 普通设置草稿：全部接入自动保存（M5.2G）----
        // 文本/数字输入：按键去抖（约 400ms）+ 失焦/Enter 立即提交；
        // 开关与下拉：变更立即提交。非法值保持草稿，不覆盖已持久化有效值。
        public string TimeoutSecondsText
        {
            get => _timeoutSecondsText;
            set { if (SetProperty(ref _timeoutSecondsText, value)) QueueAutoSave(); }
        }

        public string MaxLogLengthText
        {
            get => _maxLogLengthText;
            set { if (SetProperty(ref _maxLogLengthText, value)) QueueAutoSave(); }
        }

        public bool AutoSaveDiagnosisHistory
        {
            get => _autoSaveDiagnosisHistory;
            set { if (SetProperty(ref _autoSaveDiagnosisHistory, value)) CommitAutoSaveImmediately(); }
        }

        /// <summary>诊断录制采样间隔（毫秒）；合法值 1000/2000/5000。</summary>
        public int RecordingIntervalMs
        {
            get => _recordingIntervalMs;
            set { if (SetProperty(ref _recordingIntervalMs, value)) CommitAutoSaveImmediately(); }
        }

        public bool VoiceEnabled
        {
            get => _voiceEnabled;
            set { if (SetProperty(ref _voiceEnabled, value)) CommitAutoSaveImmediately(); }
        }

        public string VoiceEndpoint
        {
            get => _voiceEndpoint;
            set { if (SetProperty(ref _voiceEndpoint, value)) QueueAutoSave(); }
        }

        public string VoiceReferenceAudioPath
        {
            get => _voiceReferenceAudioPath;
            set { if (SetProperty(ref _voiceReferenceAudioPath, value)) QueueAutoSave(); }
        }

        public string VoicePromptText
        {
            get => _voicePromptText;
            set { if (SetProperty(ref _voicePromptText, value)) QueueAutoSave(); }
        }

        /// <summary>V2-M3.1：参考音频语言（zh / ja / en，范围由 Validator 权威定义）。</summary>
        public string VoicePromptLang
        {
            get => _voicePromptLang;
            set { if (SetProperty(ref _voicePromptLang, value)) CommitAutoSaveImmediately(); }
        }

        /// <summary>语速文本（0.7–1.3），保存时统一解析与校验。</summary>
        public string VoiceSpeedFactorText
        {
            get => _voiceSpeedFactorText;
            set { if (SetProperty(ref _voiceSpeedFactorText, value)) QueueAutoSave(); }
        }

        public string LocalDataDirectory => _localDataDirectoryService.DirectoryPath;

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (SetProperty(ref _isSaving, value))
                {
                    _exportSettingsCommand.NotifyCanExecuteChanged();
                    _importSettingsCommand.NotifyCanExecuteChanged();
                    _exportProvidersCommand.NotifyCanExecuteChanged();
                    _importProvidersCommand.NotifyCanExecuteChanged();
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

        public AsyncRelayCommand ExportSettingsCommand => _exportSettingsCommand;

        public AsyncRelayCommand ImportSettingsCommand => _importSettingsCommand;

        public AsyncRelayCommand ExportProvidersCommand => _exportProvidersCommand;

        public AsyncRelayCommand ImportProvidersCommand => _importProvidersCommand;

        public RelayCommand OpenDataDirectoryCommand { get; }

        // ---- M5.2G：自动保存 ----
        // 普通设置变化 → 校验 → 原子写盘 → 换入运行时快照。
        // 文本输入经约 400ms 去抖合并写入；开关/下拉立即提交；
        // 失焦 / Enter 立即提交。串行化在 _autoSaveChain 上，写入永不并发。
        private const int AutoSaveDebounceMilliseconds = 400;

        private DispatcherTimer? _autoSaveDebounceTimer;
        private Task _autoSaveChain = Task.CompletedTask;
        private bool _autoSaveDirty;
        private bool _suppressAutoSave;

        /// <summary>标记有未保存更改，并重启去抖计时（快速连续输入合并为一次写盘）。</summary>
        private void QueueAutoSave()
        {
            if (_suppressAutoSave)
            {
                return;
            }

            _autoSaveDirty = true;
            var timer = _autoSaveDebounceTimer;
            if (timer is null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(AutoSaveDebounceMilliseconds)
                };
                timer.Tick += (_, _) => _ = CommitAutoSaveAsync();
                _autoSaveDebounceTimer = timer;
            }
            else
            {
                timer.Stop();
            }

            timer.Start();
        }

        /// <summary>开关/下拉类变更：跳过去抖立即提交。</summary>
        private void CommitAutoSaveImmediately()
        {
            if (_suppressAutoSave)
            {
                return;
            }

            _autoSaveDirty = true;
            _ = CommitAutoSaveAsync();
        }

        /// <summary>
        /// 立即尝试提交当前草稿（失焦 / Enter / 去抖到期共用）。
        /// 无未保存更改时返回当前链尾；否则把本次提交串到链尾。
        /// </summary>
        public Task CommitAutoSaveAsync()
        {
            _autoSaveDebounceTimer?.Stop();
            if (!_autoSaveDirty)
            {
                return _autoSaveChain;
            }

            _autoSaveDirty = false;
            var previous = _autoSaveChain;
            _autoSaveChain = RunAutoSaveChainedAsync(previous);
            return _autoSaveChain;
        }

        private async Task RunAutoSaveChainedAsync(Task previous)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // 上一轮失败已经体现在状态条；本轮继续按当前草稿尝试。
            }

            await RunAutoSaveCoreAsync().ConfigureAwait(false);
        }

        private async Task RunAutoSaveCoreAsync()
        {
            if (!TryBuildSettingsFromDrafts(out var settings, out var validationMessage))
            {
                // 非法草稿：保持错误状态，绝不覆盖最后有效的持久化值。
                SetStatus(
                    SettingsStatusKind.Warning,
                    "自动保存已暂停：" + validationMessage + "（最后有效设置保持不变）");
                return;
            }

            try
            {
                // 先写盘（服务内部再次权威校验），成功后才换入运行时快照。
                // V2-M5.1B（Gate N）：旧 Ollama 字段不再来自 UI——保存时原样带回旧值，保持升级兼容。
                await _settingsService.SaveAsync(settings);
                ReplaceRuntimeConfiguration(settings);

                SetStatus(SettingsStatusKind.Success, "已自动保存，下一次诊断立即生效。");
            }
            catch (ApplicationSettingsException exception)
            {
                SetStatus(SettingsStatusKind.Error, "保存失败：" + exception.Message);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SettingsViewModel.AutoSave");
                SetStatus(SettingsStatusKind.Error, "设置自动保存失败，请稍后重试。");
            }
        }

        private async Task ExportSettingsAsync()
        {
            if (_settingsPortability is null || _fileDialogs is null)
            {
                SetStatus(SettingsStatusKind.Warning, "设置导出在此环境未启用。");
                return;
            }

            var path = _fileDialogs.PickSaveFile(
                "导出应用设置",
                "AIGeekTuner 设置 (*.json)|*.json|JSON 文件 (*.json)|*.json",
                "AIGeekTuner_Settings_v2.json");
            if (path is null)
            {
                return;
            }

            IsSaving = true;
            try
            {
                await _settingsPortability.ExportAsync(path);
                SetStatus(SettingsStatusKind.Success, "应用设置已导出；Provider 配置请在下方单独导出。");
            }
            catch (SettingsPortabilityException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
                ExceptionLogWriter.Write(exception, "Settings export");
            }
            catch (Exception exception)
            {
                SetStatus(SettingsStatusKind.Error, "应用设置导出失败，请稍后重试。");
                ExceptionLogWriter.Write(exception, "Settings export");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private async Task ImportSettingsAsync()
        {
            if (_settingsPortability is null || _fileDialogs is null)
            {
                SetStatus(SettingsStatusKind.Warning, "设置导入在此环境未启用。");
                return;
            }

            var path = _fileDialogs.PickOpenFile(
                "导入应用设置",
                "AIGeekTuner 设置 (*.json)|*.json|JSON 文件 (*.json)|*.json");
            if (path is null)
            {
                return;
            }

            if (!Confirm("导入应用设置", "导入将更新当前应用设置，是否继续？"))
            {
                return;
            }

            IsSaving = true;
            try
            {
                var result = await _settingsPortability.ImportAsync(path);
                ApplySettingsToDraft(result.Settings);
                ReplaceRuntimeConfiguration(result.Settings);
                var warning = result.Warnings.Count == 0
                    ? string.Empty
                    : " " + string.Join(" ", result.Warnings);
                SetStatus(SettingsStatusKind.Success, "应用设置已导入并保存。" + warning);
            }
            catch (SettingsPortabilityException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
                ExceptionLogWriter.Write(exception, "Settings import");
            }
            catch (Exception exception)
            {
                SetStatus(SettingsStatusKind.Error, "应用设置导入失败，当前设置未改变。");
                ExceptionLogWriter.Write(exception, "Settings import");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private async Task ExportProvidersAsync()
        {
            if (_providerPortability is null || _fileDialogs is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 导出在此环境未启用。");
                return;
            }

            var path = _fileDialogs.PickSaveFile(
                "导出 Provider 配置",
                "AIGeekTuner Provider (*.json)|*.json|JSON 文件 (*.json)|*.json",
                "AIGeekTuner_AIProviders_v1.json");
            if (path is null)
            {
                return;
            }

            IsSaving = true;
            try
            {
                await _providerPortability.ExportAsync(path);
                SetStatus(SettingsStatusKind.Success, "Provider 配置已导出；API Key 不包含在备份中。");
            }
            catch (AiProviderPortabilityException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
                ExceptionLogWriter.Write(exception, "Provider export");
            }
            catch (Exception exception)
            {
                SetStatus(SettingsStatusKind.Error, "Provider 配置导出失败，请稍后重试。");
                ExceptionLogWriter.Write(exception, "Provider export");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private async Task ImportProvidersAsync()
        {
            if (_providerPortability is null || _fileDialogs is null)
            {
                SetStatus(SettingsStatusKind.Warning, "Provider 导入在此环境未启用。");
                return;
            }

            var path = _fileDialogs.PickOpenFile(
                "导入 Provider 配置",
                "AIGeekTuner Provider (*.json)|*.json|JSON 文件 (*.json)|*.json");
            if (path is null)
            {
                return;
            }

            if (!Confirm("导入 Provider 配置", "将合并 Provider 配置，不会导入或删除 API Key。是否继续？"))
            {
                return;
            }

            IsSaving = true;
            try
            {
                var result = await _providerPortability.ImportAsync(path);
                Providers.ReloadFromPersistence();
                var warning = result.MissingCredentialProviderIds.Count == 0
                    ? string.Empty
                    : " 缺少 API Key 的 Provider：" + string.Join(", ", result.MissingCredentialProviderIds);
                SetStatus(SettingsStatusKind.Success, "Provider 配置已合并导入；本机凭据保持不变。" + warning);
            }
            catch (AiProviderPortabilityException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
                ExceptionLogWriter.Write(exception, "Provider import");
            }
            catch (Exception exception)
            {
                SetStatus(SettingsStatusKind.Error, "Provider 配置导入失败，当前配置未改变。");
                ExceptionLogWriter.Write(exception, "Provider import");
            }
            finally
            {
                IsSaving = false;
            }
        }

        private bool Confirm(string title, string message) =>
            _confirmationDialog?.Confirm(title, message) ?? true;

        private void ApplySettingsToDraft(ApplicationSettings settings)
        {
            // 导入路径显式保存；草稿回填期间不得触发自动保存。
            _suppressAutoSave = true;
            try
            {
            _timeoutSecondsText = settings.OllamaTimeoutSeconds.ToString();
            _maxLogLengthText = settings.MaxFaultLogCharacters.ToString("N0");
            _autoSaveDiagnosisHistory = settings.AutoSaveDiagnosisHistory;
            RecordingIntervalMs = settings.RecordingIntervalMs;
            VoiceEnabled = settings.Voice.Enabled;
            VoiceEndpoint = settings.Voice.Endpoint;
            VoiceReferenceAudioPath = settings.Voice.ReferenceAudioPath;
            VoicePromptText = settings.Voice.PromptText;
            VoicePromptLang = settings.Voice.PromptLang;
            VoiceSpeedFactorText = settings.Voice.SpeedFactor.ToString(
                "0.0##", System.Globalization.CultureInfo.InvariantCulture);

            OnPropertyChanged(nameof(TimeoutSecondsText));
            OnPropertyChanged(nameof(MaxLogLengthText));
            OnPropertyChanged(nameof(AutoSaveDiagnosisHistory));
            OnPropertyChanged(nameof(RecordingIntervalMs));
            OnPropertyChanged(nameof(VoiceEnabled));
            OnPropertyChanged(nameof(VoiceEndpoint));
            OnPropertyChanged(nameof(VoiceReferenceAudioPath));
            OnPropertyChanged(nameof(VoicePromptText));
            OnPropertyChanged(nameof(VoicePromptLang));
            OnPropertyChanged(nameof(VoiceSpeedFactorText));
            }
            finally
            {
                _suppressAutoSave = false;
            }
        }

        private void ReplaceRuntimeConfiguration(ApplicationSettings settings) =>
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
                },
                HardwareAutoRefresh = previous.HardwareAutoRefresh,
                HardwareRefreshIntervalMs = previous.HardwareRefreshIntervalMs
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