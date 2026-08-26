using System.Diagnostics;
using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Context;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Dialogs;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.Files;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.History;
using AIGeekTuner.Services.Knowledge;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.Services.Settings;

namespace AIGeekTuner.ViewModels
{
    public sealed class DiagnosisViewModel : ViewModelBase
    {
        private readonly INavigationService _navigationService;
        private readonly IFilePickerService _filePickerService;
        private readonly IFileReaderService _fileReaderService;
        private readonly IHardwareDetectionService _hardwareDetectionService;
        private readonly IDiagnosisService _diagnosisService;
        private readonly IDiagnosisHistoryService _diagnosisHistoryService;
        private readonly ISystemContextCollector _systemContextCollector;
        private readonly IDiagnosticKnowledgeService _knowledgeService;
        private readonly IApplicationSettingsService _applicationSettingsService;
        private readonly DiagnosticConfigurationStore _configurationStore;
        private readonly LatestDiagnosisState _latestDiagnosisState;
        private readonly AsyncRelayCommand _selectFileCommand;
        private readonly AsyncRelayCommand _startDiagnosisCommand;
        private readonly RelayCommand _cancelCommand;

        private CancellationTokenSource? _operationCancellation;
        private FaultLog? _selectedFaultLog;
        private string _faultLogText = string.Empty;
        private string _userDescription = string.Empty;
        private string? _selectedFileName;
        private string _logInputStatus = "可粘贴日志，或选择 .txt / .log 文件";
        private string _statusMessage = "等待输入故障日志";
        private string _phaseText = "等待诊断";
        private string _hardwareStepStatus = "等待";
        private string _aiStepStatus = "等待";
        private string _safetyStepStatus = "等待";
        private string? _errorMessage;
        private bool _hasError;
        private bool _isLoading;
        private bool _isApplyingFileContent;

        public DiagnosisViewModel(
            INavigationService navigationService,
            IFilePickerService filePickerService,
            IFileReaderService fileReaderService,
            IHardwareDetectionService hardwareDetectionService,
            IDiagnosisService diagnosisService,
            IDiagnosisHistoryService diagnosisHistoryService,
            ISystemContextCollector systemContextCollector,
            IDiagnosticKnowledgeService knowledgeService,
            IApplicationSettingsService applicationSettingsService,
            DiagnosticConfigurationStore configurationStore,
            LatestDiagnosisState latestDiagnosisState)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));
            _fileReaderService = fileReaderService ?? throw new ArgumentNullException(nameof(fileReaderService));
            _hardwareDetectionService = hardwareDetectionService ?? throw new ArgumentNullException(nameof(hardwareDetectionService));
            _diagnosisService = diagnosisService ?? throw new ArgumentNullException(nameof(diagnosisService));
            _diagnosisHistoryService = diagnosisHistoryService ?? throw new ArgumentNullException(nameof(diagnosisHistoryService));
            _systemContextCollector = systemContextCollector ?? throw new ArgumentNullException(nameof(systemContextCollector));
            _knowledgeService = knowledgeService ?? throw new ArgumentNullException(nameof(knowledgeService));
            _applicationSettingsService = applicationSettingsService
                ?? throw new ArgumentNullException(nameof(applicationSettingsService));
            _configurationStore = configurationStore
                ?? throw new ArgumentNullException(nameof(configurationStore));
            _latestDiagnosisState = latestDiagnosisState ?? throw new ArgumentNullException(nameof(latestDiagnosisState));

            _selectFileCommand = new AsyncRelayCommand(SelectFileAsync, () => !IsLoading);
            _startDiagnosisCommand = new AsyncRelayCommand(
                StartDiagnosisAsync,
                () => !IsLoading && !string.IsNullOrWhiteSpace(FaultLogText));
            _cancelCommand = new RelayCommand(Cancel, () => IsLoading);
        }

        public string FaultLogText
        {
            get => _faultLogText;
            set
            {
                if (!SetProperty(ref _faultLogText, value)) return;
                if (!_isApplyingFileContent)
                {
                    _selectedFaultLog = null;
                    SelectedFileName = null;
                    LogInputStatus = string.IsNullOrWhiteSpace(value)
                        ? "可粘贴日志，或选择 .txt / .log 文件"
                        : "正在使用粘贴或编辑后的日志文本";
                }
                _startDiagnosisCommand.NotifyCanExecuteChanged();
            }
        }

        public string UserDescription
        {
            get => _userDescription;
            set => SetProperty(ref _userDescription, value);
        }

        public string? SelectedFileName { get => _selectedFileName; private set => SetProperty(ref _selectedFileName, value); }
        public string LogInputStatus { get => _logInputStatus; private set => SetProperty(ref _logInputStatus, value); }
        public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
        public string PhaseText { get => _phaseText; private set => SetProperty(ref _phaseText, value); }
        public string HardwareStepStatus { get => _hardwareStepStatus; private set => SetProperty(ref _hardwareStepStatus, value); }
        public string AiStepStatus { get => _aiStepStatus; private set => SetProperty(ref _aiStepStatus, value); }
        public string SafetyStepStatus { get => _safetyStepStatus; private set => SetProperty(ref _safetyStepStatus, value); }
        public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
        public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (!SetProperty(ref _isLoading, value)) return;
                _selectFileCommand.NotifyCanExecuteChanged();
                _startDiagnosisCommand.NotifyCanExecuteChanged();
                _cancelCommand.NotifyCanExecuteChanged();
            }
        }

        public AsyncRelayCommand SelectFileCommand => _selectFileCommand;
        public AsyncRelayCommand StartDiagnosisCommand => _startDiagnosisCommand;
        public ICommand CancelCommand => _cancelCommand;

        private async Task SelectFileAsync()
        {
            ClearError();
            var path = _filePickerService.PickFaultLogFile();
            if (path is null)
            {
                StatusMessage = "已取消文件选择";
                return;
            }

            await RunOperationAsync(async cancellationToken =>
            {
                StatusMessage = "正在读取故障日志...";
                var faultLog = await _fileReaderService.ReadAsync(path, cancellationToken);
                _selectedFaultLog = faultLog;
                _isApplyingFileContent = true;
                try { FaultLogText = faultLog.Content; }
                finally { _isApplyingFileContent = false; }

                SelectedFileName = faultLog.FileName;
                LogInputStatus = $"{faultLog.FileName} · {FormatFileSize(faultLog.FileSizeBytes)} · {faultLog.EncodingName ?? "Unknown"}";
                StatusMessage = "日志读取完成，可以开始诊断";
            });
        }

        private async Task StartDiagnosisAsync()
        {
            ClearError();
            PhaseText = "正在分析";
            HardwareStepStatus = "读取中";
            AiStepStatus = "等待";
            SafetyStepStatus = "等待";

            await RunOperationAsync(async cancellationToken =>
            {
                // 真实诊断入口开始计时（含硬件/readiness/AI/Safety 全程）。
                var diagnosisStopwatch = Stopwatch.StartNew();

                var faultLog = CreateFaultLog();
                StatusMessage = "正在读取本机真实硬件信息...";
                var hardware = await _hardwareDetectionService.DetectAsync(cancellationToken);
                var systemContext = await CollectSystemContextSafelyAsync(
                    cancellationToken);
                var knowledgeContext = await FindKnowledgeSafelyAsync(
                    faultLog,
                    cancellationToken);

                HardwareStepStatus = "已完成";
                AiStepStatus = "分析中";
                SafetyStepStatus = "等待 AI 结果";
                StatusMessage = "正在调用本地 Ollama；返回后将自动执行 SafetyGuard...";

                // 本次诊断的唯一配置快照：readiness、prompt、模型请求共用。
                var configuration = _configurationStore.Snapshot();
                var request = new DiagnosticRequest
                {
                    Hardware = hardware,
                    FaultLog = faultLog,
                    SystemContext = systemContext,
                    KnowledgeContext = knowledgeContext,
                    RequestedAt = DateTimeOffset.UtcNow
                };
                DiagnosisOutcome outcome;
                try
                {
                    outcome = await _diagnosisService.DiagnoseAsync(
                        request,
                        configuration,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    // 用户主动取消不是系统失败：不写入历史。
                    throw;
                }
                catch (DiagnosisException exception)
                    when (!DiagnosisFailurePolicy.ShouldPersistFailure(exception.Error))
                {
                    // 输入校验类问题不生成历史记录。
                    throw;
                }
                catch (DiagnosisException exception)
                {
                    await TrySaveFailureHistorySilentlyAsync(
                        exception.Error.ToString(),
                        exception.Message,
                        configuration,
                        diagnosisStopwatch.ElapsedMilliseconds,
                        cancellationToken);
                    throw;
                }

                AiStepStatus = "已完成";
                SafetyStepStatus = "已完成";
                PhaseText = "诊断完成";
                _latestDiagnosisState.Outcome = outcome;
                if (_applicationSettingsService.Current.AutoSaveDiagnosisHistory)
                {
                    StatusMessage = "诊断完成，正在保存本地报告...";
                    await _diagnosisHistoryService.SaveSuccessAsync(
                        outcome,
                        configuration.Ollama.ModelName,
                        diagnosisStopwatch.ElapsedMilliseconds,
                        cancellationToken);
                    StatusMessage = "诊断、安全检查与本地报告保存已完成";
                }
                else
                {
                    StatusMessage = "诊断与安全检查已完成；本次报告未自动保存";
                }

                if (_navigationService.IsCurrent(AppPage.Diagnosis))
                {
                    _navigationService.NavigateTo(AppPage.Result, outcome);
                }
                else
                {
                    // 用户已离开诊断页：不强行拉回；
                    // 结果已写入 LatestDiagnosisState，可通过侧栏「诊断报告」入口查看。
                    StatusMessage = "诊断已完成，结果已保留在「诊断报告」入口";
                }
            });
        }

        private async Task TrySaveFailureHistorySilentlyAsync(
            string failureCode,
            string failureReason,
            DiagnosticConfiguration configuration,
            long elapsedMs,
            CancellationToken cancellationToken)
        {
            // AutoSave 关闭时成功与失败都不落库，保持语义一致。
            if (!_applicationSettingsService.Current.AutoSaveDiagnosisHistory)
            {
                return;
            }

            try
            {
                await _diagnosisHistoryService.SaveFailureAsync(
                    new DiagnosisFailureInfo(
                        DateTimeOffset.Now,
                        configuration.Ollama.ModelName,
                        elapsedMs,
                        failureCode,
                        failureReason,
                        _selectedFaultLog?.FileName),
                    cancellationToken);
            }
            catch (Exception saveFailure)
            {
                // 失败留痕属于尽力而为；任何保存异常不得掩盖原始诊断错误。
                ExceptionLogWriter.Write(saveFailure, "DiagnosisViewModel.SaveFailureHistory");
            }
        }

        private FaultLog CreateFaultLog()
        {
            var combinedContent = string.IsNullOrWhiteSpace(UserDescription)
                ? FaultLogText
                : $"{FaultLogText.TrimEnd()}\n\n[用户描述]\n{UserDescription.Trim()}";

            if (_selectedFaultLog is null)
            {
                return FaultLog.FromPastedText(combinedContent);
            }

            return new FaultLog
            {
                FileName = _selectedFaultLog.FileName,
                Content = combinedContent,
                FileSizeBytes = _selectedFaultLog.FileSizeBytes,
                CreatedAt = _selectedFaultLog.CreatedAt,
                SourceType = _selectedFaultLog.SourceType,
                EncodingName = _selectedFaultLog.EncodingName
            };
        }

        private async Task<AIGeekTuner.Models.SystemContext> CollectSystemContextSafelyAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await _systemContextCollector.CollectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new AIGeekTuner.Models.SystemContext();
            }
        }

        private async Task<IReadOnlyList<DiagnosticKnowledgeEntry>> FindKnowledgeSafelyAsync(
            FaultLog faultLog,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _knowledgeService.FindMatchesAsync(
                    faultLog,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<DiagnosticKnowledgeEntry>();
            }
        }

        private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
        {
            using var cancellationSource = new CancellationTokenSource();
            _operationCancellation = cancellationSource;
            IsLoading = true;
            try
            {
                await operation(cancellationSource.Token);
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
                PhaseText = "已取消";
                StatusMessage = "操作已取消";
                MarkActiveStepCancelled();
            }
            catch (FaultLogReadException exception) { SetError(exception.Message); }
            catch (DiagnosisException exception) { SetDiagnosisError(exception); }
            catch (DiagnosisHistoryException exception) { SetHistoryError(exception.Message); }
            catch (Exception) { SetError("操作失败，请稍后重试"); }
            finally
            {
                if (ReferenceEquals(_operationCancellation, cancellationSource)) _operationCancellation = null;
                IsLoading = false;
            }
        }

        private void Cancel()
        {
            StatusMessage = "正在取消操作...";
            _operationCancellation?.Cancel();
        }

        private void ClearError()
        {
            ErrorMessage = null;
            HasError = false;
        }

        private void SetError(string message)
        {
            ErrorMessage = message;
            HasError = true;
            PhaseText = "诊断失败";
            StatusMessage = "诊断未完成";

            if (HardwareStepStatus == "读取中")
            {
                HardwareStepStatus = "失败";
            }
            else if (AiStepStatus == "分析中")
            {
                AiStepStatus = "失败";
                SafetyStepStatus = "未执行";
            }
        }

        private void SetDiagnosisError(DiagnosisException exception)
        {
            if (exception.Error == DiagnosisError.HardwareUnavailable)
            {
                HardwareStepStatus = "失败";
                AiStepStatus = "未执行";
                SafetyStepStatus = "未执行";
            }
            else if (exception.Error == DiagnosisError.SafetyCheckFailed)
            {
                AiStepStatus = "已完成";
                SafetyStepStatus = "失败";
            }

            SetError(exception.Message);
        }

        private void SetHistoryError(string message)
        {
            ErrorMessage = message;
            HasError = true;
            PhaseText = "诊断完成 · 保存失败";
            StatusMessage = "诊断结果仍可通过“诊断报告”入口查看";
        }

        private void MarkActiveStepCancelled()
        {
            if (HardwareStepStatus == "读取中")
            {
                HardwareStepStatus = "已取消";
            }
            else if (AiStepStatus == "分析中")
            {
                AiStepStatus = "已取消";
                SafetyStepStatus = "未执行";
            }
        }

        private static string FormatFileSize(long? bytes)
        {
            if (bytes is null or < 0)
            {
                return "大小未知";
            }

            if (bytes < 1024)
            {
                return $"{bytes} B";
            }

            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024d:0.#} KB";
            }

            return $"{bytes / 1024d / 1024d:0.#} MB";
        }
    }
}
