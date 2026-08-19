using System.Collections.ObjectModel;
using System.Windows;
using AIGeekTuner.Commands;
using AIGeekTuner.Models;
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
                _ => !IsLoading);
            _deleteRecordCommand = new AsyncRelayCommand<DiagnosisRecord>(
                DeleteRecordAsync,
                _ => !IsLoading);
            _exportRecordCommand = new AsyncRelayCommand<DiagnosisRecord>(
                ExportRecordAsync,
                _ => !IsLoading);
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
                var outcome = await _historyService.LoadOutcomeAsync(
                    record,
                    CancellationToken.None);
                await _reportExportService.ExportAsync(
                    outcome,
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
            catch
            {
                ErrorMessage = "报告导出失败，请稍后重试。";
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
                var outcome = await _historyService.LoadOutcomeAsync(
                    record,
                    CancellationToken.None);
                _latestDiagnosisState.Outcome = outcome;
                _navigationService.NavigateTo(AppPage.Result, outcome);
            }
            catch (DiagnosisHistoryException exception)
            {
                ErrorMessage = exception.Message;
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
