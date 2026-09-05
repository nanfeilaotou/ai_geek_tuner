using System.Windows;
using System.Windows.Input;
using AIGeekTuner.Commands;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.Services.Reports;

namespace AIGeekTuner.ViewModels
{
    public sealed class ResultViewModel : ViewModelBase
    {
        private readonly IReportExportService _reportExportService;
        private readonly AsyncRelayCommand _exportReportCommand;
        private string? _exportStatusMessage;

        public ResultViewModel(
            INavigationService navigationService,
            IReportExportService reportExportService,
            DiagnosisOutcome? outcome = null)
        {
            ArgumentNullException.ThrowIfNull(navigationService);
            _reportExportService = reportExportService
                ?? throw new ArgumentNullException(nameof(reportExportService));
            Outcome = outcome;
            BackCommand = new RelayCommand(navigationService.GoBack);
            StartNewDiagnosisCommand = new RelayCommand(
                () => navigationService.NavigateTo(AppPage.Diagnosis));
            _exportReportCommand = new AsyncRelayCommand(
                ExportReportAsync,
                () => Outcome is not null);
        }

        public DiagnosisOutcome? Outcome { get; }

        public DiagnosticResult? Result => Outcome?.AiResult;

        public SafetyResult? Safety => Outcome?.Safety;

        public string StatusMessage => Outcome is null
            ? "尚无诊断结果。"
            : $"诊断完成 · SafetyGuard: {Outcome.Safety.Status}";

        public string ConfidenceText => Result is null
            ? string.Empty
            : Result.Confidence.ToString("P0");

        public double ConfidenceValue => Result is null
            ? 0
            : Math.Clamp(Result.Confidence, 0, 1);

        public string CompletedAtText => Outcome is null
            ? string.Empty
            : Outcome.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public IReadOnlyList<DiagnosticEvidence> FactEvidence =>
            Result?.Evidence.Where(item => item.Kind == EvidenceKind.Fact).ToArray()
            ?? Array.Empty<DiagnosticEvidence>();

        public IReadOnlyList<DiagnosticEvidence> InferenceEvidence =>
            Result?.Evidence.Where(item => item.Kind == EvidenceKind.Inference).ToArray()
            ?? Array.Empty<DiagnosticEvidence>();

        /// <summary>
        /// 新 grounding Fact 的主展示文本只能来自原始 SourceQuote；旧历史没有 quote
        /// 时才回退到 Description，以保持历史可读性。
        /// </summary>
        public IReadOnlyList<string> FactEvidenceDisplay =>
            FactEvidence.Select(FormatFactEvidence).ToArray();

        public string FactEvidenceCountText => $"{FactEvidence.Count} 条";

        public string InferenceEvidenceCountText => $"{InferenceEvidence.Count} 条";

        public Visibility DiagnosticContentVisibility =>
            Outcome is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility RecommendationsVisibility =>
            Outcome?.Safety.Status == SafetyStatus.Rejected
                ? Visibility.Collapsed
                : Outcome is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility WarningVisibility =>
            Outcome?.Safety.Status == SafetyStatus.ApprovedWithWarnings
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility ApprovedVisibility =>
            Outcome?.Safety.Status == SafetyStatus.Approved
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility RejectedVisibility =>
            Outcome?.Safety.Status == SafetyStatus.Rejected
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility EmptyVisibility => Outcome is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        public IReadOnlyList<string> SafetyMessages =>
            Safety?.Warnings ?? Array.Empty<string>();

        public string? ExportStatusMessage
        {
            get => _exportStatusMessage;
            private set => SetProperty(ref _exportStatusMessage, value);
        }

        public ICommand BackCommand { get; }

        public ICommand StartNewDiagnosisCommand { get; }

        public AsyncRelayCommand ExportReportCommand =>
            _exportReportCommand;

        private async Task ExportReportAsync()
        {
            if (Outcome is null)
            {
                return;
            }

            try
            {
                await _reportExportService.ExportAsync(
                    Outcome,
                    CancellationToken.None);
                ExportStatusMessage = "报告已导出到本地数据目录。";
            }
            catch (ReportExportException exception)
            {
                ExportStatusMessage = exception.Message;
            }
            catch
            {
                ExportStatusMessage = "报告导出失败，请稍后重试。";
            }
        }

        private string FormatFactEvidence(DiagnosticEvidence evidence)
        {
            if (string.IsNullOrWhiteSpace(evidence.SourceQuote))
            {
                return evidence.Description;
            }

            return $"[{GetSourceLabel(evidence.SourceId)}] {evidence.SourceQuote.Trim()}";
        }

        private static string GetSourceLabel(string? sourceId) => sourceId switch
        {
            DiagnosticEvidenceSourceIds.UserDescription => "用户描述",
            DiagnosticEvidenceSourceIds.FaultLog => "故障日志",
            DiagnosticEvidenceSourceIds.HardwareContext => "硬件上下文",
            DiagnosticEvidenceSourceIds.SystemContext => "系统上下文",
            _ => "输入来源"
        };
    }
}
