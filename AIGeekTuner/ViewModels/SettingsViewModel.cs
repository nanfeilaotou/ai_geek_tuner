using System.Collections.ObjectModel;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Settings;

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
        private readonly IOllamaConnectionService _connectionService;
        private readonly DiagnosticConfigurationStore _configurationStore;
        private readonly ILocalDataDirectoryService _localDataDirectoryService;

        private readonly AsyncRelayCommand _saveCommand;
        private readonly AsyncRelayCommand _refreshModelsCommand;
        private readonly AsyncRelayCommand _testConnectionCommand;

        private string _baseUrl;
        private string _modelName;
        private string _timeoutSecondsText;
        private string _maxLogLengthText;
        private bool _useJsonFormat;
        private bool _autoSaveDiagnosisHistory;

        private bool _isSaving;
        private bool _isLoadingModels;
        private bool _isTestingConnection;

        private string _statusMessage = "更改完成后点击“保存设置”。连接检测与保存互不影响。";
        private SettingsStatusKind _statusKind = SettingsStatusKind.Info;

        public SettingsViewModel(
            IApplicationSettingsService settingsService,
            IOllamaConnectionService connectionService,
            DiagnosticConfigurationStore configurationStore,
            ILocalDataDirectoryService localDataDirectoryService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
            _localDataDirectoryService = localDataDirectoryService ?? throw new ArgumentNullException(nameof(localDataDirectoryService));

            var current = settingsService.Current;
            _baseUrl = current.OllamaBaseUrl;
            _modelName = current.OllamaModelName;
            _timeoutSecondsText = current.OllamaTimeoutSeconds.ToString();
            _maxLogLengthText = current.MaxFaultLogCharacters.ToString("N0");
            _useJsonFormat = current.UseJsonFormat;
            _autoSaveDiagnosisHistory = current.AutoSaveDiagnosisHistory;

            _saveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving);
            _refreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, () => !IsLoadingModels);
            _testConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsTestingConnection);
            OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set => SetProperty(ref _baseUrl, value);
        }

        public string ModelName
        {
            get => _modelName;
            set => SetProperty(ref _modelName, value);
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

        public bool UseJsonFormat
        {
            get => _useJsonFormat;
            set => SetProperty(ref _useJsonFormat, value);
        }

        public bool AutoSaveDiagnosisHistory
        {
            get => _autoSaveDiagnosisHistory;
            set => SetProperty(ref _autoSaveDiagnosisHistory, value);
        }

        public ObservableCollection<string> AvailableModels { get; } = [];

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

        public bool IsLoadingModels
        {
            get => _isLoadingModels;
            private set
            {
                if (SetProperty(ref _isLoadingModels, value))
                {
                    _refreshModelsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            private set
            {
                if (SetProperty(ref _isTestingConnection, value))
                {
                    _testConnectionCommand.NotifyCanExecuteChanged();
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

        public AsyncRelayCommand RefreshModelsCommand => _refreshModelsCommand;

        public AsyncRelayCommand TestConnectionCommand => _testConnectionCommand;

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

        private async Task RefreshModelsAsync()
        {
            IsLoadingModels = true;
            try
            {
                var models = await _connectionService.GetModelsAsync(BaseUrl);

                // 刷新只更新候选列表；用户手动输入的名称绝不能被清掉。
                AvailableModels.Clear();
                foreach (var model in models)
                {
                    AvailableModels.Add(model);
                }

                SetStatus(SettingsStatusKind.Success, $"已从 Ollama 获取 {models.Count} 个已安装模型。");
            }
            catch (OllamaConnectionException exception)
            {
                SetStatus(SettingsStatusKind.Warning, exception.Message);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SettingsViewModel.RefreshModelsAsync");
                SetStatus(SettingsStatusKind.Error, "获取模型列表失败，请稍后重试。");
            }
            finally
            {
                IsLoadingModels = false;
            }
        }

        private async Task TestConnectionAsync()
        {
            IsTestingConnection = true;
            try
            {
                var modelName = ModelName.Trim();
                if (string.IsNullOrEmpty(modelName))
                {
                    SetStatus(SettingsStatusKind.Warning, "请先填写要使用的模型名称。");
                    return;
                }

                var readiness = await _connectionService.CheckReadinessAsync(BaseUrl, modelName);
                SetStatus(ToStatusKind(readiness.Status), readiness.Message);

                if (readiness.Models.Count > 0 && AvailableModels.Count == 0)
                {
                    foreach (var model in readiness.Models)
                    {
                        AvailableModels.Add(model);
                    }
                }
            }
            catch (OllamaConnectionException exception)
            {
                SetStatus(SettingsStatusKind.Error, exception.Message);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SettingsViewModel.TestConnectionAsync");
                SetStatus(SettingsStatusKind.Error, "连接检测失败，请稍后重试。");
            }
            finally
            {
                IsTestingConnection = false;
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

            settings = new ApplicationSettings
            {
                AutoSaveDiagnosisHistory = AutoSaveDiagnosisHistory,
                OllamaBaseUrl = BaseUrl.Trim(),
                OllamaModelName = ModelName.Trim(),
                OllamaTimeoutSeconds = int.Parse(TimeoutSecondsText),
                MaxFaultLogCharacters = int.Parse(MaxLogLengthText, System.Globalization.NumberStyles.AllowThousands),
                UseJsonFormat = UseJsonFormat
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

        private void SetStatus(SettingsStatusKind kind, string message)
        {
            StatusKind = kind;
            StatusMessage = message;
        }

        private static SettingsStatusKind ToStatusKind(OllamaReadinessStatus status) =>
            status switch
            {
                OllamaReadinessStatus.Ready => SettingsStatusKind.Success,
                OllamaReadinessStatus.ModelMissing => SettingsStatusKind.Warning,
                _ => SettingsStatusKind.Error
            };
    }
}
