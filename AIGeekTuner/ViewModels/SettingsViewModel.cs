using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Settings;

namespace AIGeekTuner.ViewModels
{
    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly IAiService _aiService;
        private readonly IApplicationSettingsService _applicationSettingsService;
        private readonly ILocalDataDirectoryService _localDataDirectoryService;
        private readonly AsyncRelayCommand _testConnectionCommand;
        private readonly AsyncRelayCommand<bool> _setAutoSaveCommand;

        private string _ollamaStatus = "未检测";
        private DateTimeOffset? _lastCheckedAtUtc;
        private bool _isChecking;
        private bool _autoSaveDiagnosisHistory;
        private bool _isSavingSettings;
        private string _settingsSaveStatus = "更改后自动保存到本机";
        private string? _dataDirectoryStatus;

        public SettingsViewModel(
            OllamaOptions options,
            DiagnosisInputOptions inputOptions,
            IAiService aiService,
            IApplicationSettingsService applicationSettingsService,
            ILocalDataDirectoryService localDataDirectoryService)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(inputOptions);
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
            _applicationSettingsService = applicationSettingsService
                ?? throw new ArgumentNullException(nameof(applicationSettingsService));
            _localDataDirectoryService = localDataDirectoryService
                ?? throw new ArgumentNullException(nameof(localDataDirectoryService));

            BaseUrl = options.BaseUrl;
            ModelName = options.ModelName;
            Timeout = $"{options.TimeoutSeconds} 秒";
            OutputLanguage = inputOptions.OutputLanguage;
            MaxLogLength = $"{inputOptions.MaxFaultLogCharacters:N0} 字符";
            _autoSaveDiagnosisHistory =
                _applicationSettingsService.Current.AutoSaveDiagnosisHistory;
            _testConnectionCommand = new AsyncRelayCommand(
                TestConnectionAsync,
                () => !IsChecking);
            _setAutoSaveCommand = new AsyncRelayCommand<bool>(
                SetAutoSaveDiagnosisHistoryAsync,
                _ => !IsSavingSettings);
            OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
        }

        public string BaseUrl { get; }
        public string ModelName { get; }
        public string Timeout { get; }
        public string OutputLanguage { get; }
        public string MaxLogLength { get; }

        public string LocalDataDirectory =>
            _localDataDirectoryService.DirectoryPath;

        public string? DataDirectoryStatus
        {
            get => _dataDirectoryStatus;
            private set => SetProperty(ref _dataDirectoryStatus, value);
        }

        public bool AutoSaveDiagnosisHistory
        {
            get => _autoSaveDiagnosisHistory;
            private set => SetProperty(ref _autoSaveDiagnosisHistory, value);
        }

        public string SettingsSaveStatus
        {
            get => _settingsSaveStatus;
            private set => SetProperty(ref _settingsSaveStatus, value);
        }

        public bool IsSavingSettings
        {
            get => _isSavingSettings;
            private set
            {
                if (SetProperty(ref _isSavingSettings, value))
                {
                    _setAutoSaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string OllamaStatus
        {
            get => _ollamaStatus;
            private set => SetProperty(ref _ollamaStatus, value);
        }

        public string LastCheckedAtText => _lastCheckedAtUtc is null
            ? "尚未检测"
            : _lastCheckedAtUtc.Value
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");

        public bool IsChecking
        {
            get => _isChecking;
            private set
            {
                if (!SetProperty(ref _isChecking, value))
                {
                    return;
                }

                _testConnectionCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(TestConnectionButtonText));
            }
        }

        public string TestConnectionButtonText =>
            IsChecking ? "检测中..." : "测试连接";

        public AsyncRelayCommand TestConnectionCommand =>
            _testConnectionCommand;

        public AsyncRelayCommand<bool> SetAutoSaveCommand =>
            _setAutoSaveCommand;

        public RelayCommand OpenDataDirectoryCommand { get; }

        private void OpenDataDirectory()
        {
            DataDirectoryStatus = null;
            try
            {
                _localDataDirectoryService.Open();
                DataDirectoryStatus = "已打开本地数据目录";
            }
            catch (LocalDataDirectoryException exception)
            {
                DataDirectoryStatus = exception.Message;
            }
            catch
            {
                DataDirectoryStatus = "无法打开本地数据目录，请稍后重试。";
            }
        }

        private async Task SetAutoSaveDiagnosisHistoryAsync(bool enabled)
        {
            var previousValue = AutoSaveDiagnosisHistory;
            if (previousValue == enabled)
            {
                return;
            }

            IsSavingSettings = true;
            AutoSaveDiagnosisHistory = enabled;
            SettingsSaveStatus = "正在保存设置...";
            try
            {
                await _applicationSettingsService
                    .SetAutoSaveDiagnosisHistoryAsync(enabled);
                SettingsSaveStatus = "设置已保存";
            }
            catch (ApplicationSettingsException exception)
            {
                AutoSaveDiagnosisHistory = previousValue;
                SettingsSaveStatus = exception.Message;
            }
            catch
            {
                AutoSaveDiagnosisHistory = previousValue;
                SettingsSaveStatus = "设置保存失败，请稍后重试。";
            }
            finally
            {
                IsSavingSettings = false;
            }
        }

        private async Task TestConnectionAsync()
        {
            IsChecking = true;
            OllamaStatus = "检测中";
            var isAvailable = false;
            try
            {
                isAvailable = await _aiService.IsAvailableAsync();
            }
            catch
            {
                // 设置页的状态检测是非关键操作，任何连接异常都映射为离线。
                isAvailable = false;
            }
            finally
            {
                _lastCheckedAtUtc = DateTimeOffset.UtcNow;
                OnPropertyChanged(nameof(LastCheckedAtText));
                OllamaStatus = isAvailable ? "在线" : "离线";
                IsChecking = false;
            }
        }
    }
}
