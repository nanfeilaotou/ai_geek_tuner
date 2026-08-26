using System.Collections.ObjectModel;
using System.Windows;
using AIGeekTuner.Commands;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.History;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.Services.Dialogs;
using AIGeekTuner.Services.Reports;

namespace AIGeekTuner.ViewModels
{
    public sealed class DiagnosisHistoryViewModel : ViewModelBase
    {
        private readonly INavigationService _navigationService;
        private readonly IDiagnosisHistoryService _historyService;
        private readonly LatestDiagnosisState _latestDiagnosisState;
        private readonly IConfirmationDialogService _confirmationDialogService;
        private readonly IReportExportService _reportExportService;
        private readonly AsyncRelayCommand<DiagnosisRecord> _openRecordCommand;
        private readonly AsyncRelayCommand<DiagnosisRecord> _deleteRecordCommand;
        private readonly AsyncRelayCommand<DiagnosisRecord> _exportRecordCommand;
        private readonly AsyncRelayCommand _clearAllCommand;

        private bool _isLoading;
        private string? _errorMessage;
        private string? _statusMessage;

        public DiagnosisHistoryViewModel(
            INavigationService navigationService,
            IDiagnosisHistoryService historyService,
            IConfirmationDialogService confirmationDialogService,
            IReportExportService reportExportService,
            LatestDiagnosisState latestDiagnosisState)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            _confirmationDialogService = confirmationDialogService
                ?? throw new ArgumentNullException(nameof(confirmationDialogService));
            _reportExportService = reportExportService
                ?? throw new ArgumentNullException(nameof(reportExportService));
            _latestDiagnosisState = latestDiagnosisState ?? throw new ArgumentNullException(nameof(latestDiagnosisState));
            _openRecordCommand = new AsyncRelayCommand<DiagnosisRecord>(
                OpenRecordAsync,
                record => !IsLoading && record.Succeeded);
            _deleteRecordCommand = new AsyncRelayCommand<DiagnosisRecord>(
                DeleteRecordAsync,
                _ => !IsLoading);
            _exportRecordCommand = new AsyncRelayCommand<DiagnosisRecord>(
                ExportRecordAsync,
                record => !IsLoading && record.Succeeded);
            _clearAllCommand = new AsyncRelayCommand(
                ClearAllAsync,
                () => !IsLoading);
        }

        public ObservableCollection<DiagnosisRecord> Records { get; } = [];

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (!SetProperty(ref _isLoading, value))
                {
                    return;
                }

                _openRecordCommand.NotifyCanExecuteChanged();
                _deleteRecordCommand.NotifyCanExecuteChanged();
                _exportRecordCommand.NotifyCanExecuteChanged();
                OnStateChanged();
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    OnStateChanged();
                }
            }
        }

        public string? StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public Visibility LoadingVisibility =>
            IsLoading ? Visibility.Visible : Visibility.Collapsed;

        public Visibility RecordsVisibility =>
            !IsLoading && ErrorMessage is null && Records.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility EmptyVisibility =>
            !IsLoading && ErrorMessage is null && Records.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility ErrorVisibility =>
            ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;

        public AsyncRelayCommand<DiagnosisRecord> OpenRecordCommand =>
            _openRecordCommand;

        public AsyncRelayCommand<DiagnosisRecord> DeleteRecordCommand =>
            _deleteRecordCommand;

        public AsyncRelayCommand<DiagnosisRecord> ExportRecordCommand =>
            _exportRecordCommand;

        public AsyncRelayCommand ClearAllCommand =>
            _clearAllCommand;

        public async Task LoadAsync()
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var records = await _historyService.GetRecordsAsync(
                    CancellationToken.None);
                ReplaceRecords(records);
            }
            catch (DiagnosisHistoryException exception)
            {
                ErrorMessage = exception.Message;
            }
            catch (Exception exception)
            {
                // 未预期异常走页面内错误横幅，不弹窗（与业务异常同一反馈通道，避免双重提示）。
                ExceptionLogWriter.Write(exception, "DiagnosisHistoryViewModel.LoadAsync");
                ErrorMessage = "读取历史记录时发生未预期的错误，请稍后重试。";
            }
            finally
            {
                IsLoading = false;
                OnStateChanged();
            }
        }

        private async Task DeleteRecordAsync(DiagnosisRecord record)
        {
            var confirmed = _confirmationDialogService.Confirm(
                "删除诊断记录",
                $"是否删除这条诊断记录？\n\n{record.LogFileName}\n\n删除后无法恢复。");
            if (!confirmed)
            {
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                await _historyService.DeleteAsync(
                    record.DiagnosisId,
                    CancellationToken.None);
                var records = await _historyService.GetRecordsAsync(
                    CancellationToken.None);
                ReplaceRecords(records);
            }
            catch (DiagnosisHistoryException exception)
            {
                ErrorMessage = exception.Message;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "DiagnosisHistoryViewModel.DeleteRecordAsync");
                ErrorMessage = "删除历史记录时发生未预期的错误，请稍后重试。";
            }
            finally
            {
                IsLoading = false;
                OnStateChanged();
            }
        }

        private async Task ExportRecordAsync(DiagnosisRecord record)
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var detail = await _historyService.LoadDetailAsync(
                    record,
                    CancellationToken.None);
                await _reportExportService.ExportAsync(
                    detail.Outcome!,
                    CancellationToken.None);
                StatusMessage = "报告已导出到本地数据目录。";
            }
            catch (DiagnosisHistoryException exception)
            {
                ErrorMessage = exception.Message;
            }
            catch (ReportExportException exception)
            {
                ErrorMessage = exception.Message;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "DiagnosisHistoryViewModel.ExportRecordAsync");
                ErrorMessage = "报告导出失败，请稍后重试。";
            }
            finally
            {
                IsLoading = false;
                OnStateChanged();
            }
        }

        public async Task ClearAllAsync()
        {
            var confirmed = _confirmationDialogService.Confirm(
                "清空诊断历史",
                "此操作将删除全部本地诊断历史，无法撤销。确定继续吗？");
            if (!confirmed)
            {
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                await _historyService.ClearAllAsync(CancellationToken.None);
                Records.Clear();
                OnStateChanged();
                StatusMessage = "已清空全部本地诊断历史。";
            }
            catch (DiagnosisHistoryException exception)
            {
                ErrorMessage = exception.Message;
            }
            finally
            {
                IsLoading = false;
                OnStateChanged();
            }
        }

        private async Task OpenRecordAsync(DiagnosisRecord record)
        {
            IsLoading = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                var detail = await _historyService.LoadDetailAsync(
                    record,
                    CancellationToken.None);

                // 失败记录不会进入这里（CanExecute 已拦截成功项之外的打开）。
                var outcome = detail.Outcome!;
                _latestDiagnosisState.Outcome = outcome;
                _navigationService.NavigateTo(AppPage.Result, outcome);
            }
            catch (DiagnosisHistoryException exception)
            {
                ErrorMessage = exception.Message;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "DiagnosisHistoryViewModel.OpenRecordAsync");
                ErrorMessage = "打开历史报告时发生未预期的错误，请稍后重试。";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void OnStateChanged()
        {
            OnPropertyChanged(nameof(LoadingVisibility));
            OnPropertyChanged(nameof(RecordsVisibility));
            OnPropertyChanged(nameof(EmptyVisibility));
            OnPropertyChanged(nameof(ErrorVisibility));
        }

        private void ReplaceRecords(
            IReadOnlyList<DiagnosisRecord> records)
        {
            Records.Clear();
            foreach (var record in records)
            {
                Records.Add(record);
            }

            OnStateChanged();
        }
    }
}
