using System.IO;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.Services.Reports;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Tests.Services.Reports;

public sealed class GroundedEvidencePresentationTests
{
    [Fact]
    public void ResultFactsPreferSourceQuoteAndLegacyFactsFallbackToDescription()
    {
        var outcome = CreateOutcome(
            new DiagnosticEvidence
            {
                Kind = EvidenceKind.Fact,
                Description = "AI 声称用户说电箱烂了",
                SourceId = DiagnosticEvidenceSourceIds.FaultLog,
                SourceQuote = "我电脑卡了"
            },
            new DiagnosticEvidence
            {
                Kind = EvidenceKind.Fact,
                Description = "旧历史事实"
            });
        var viewModel = new ResultViewModel(
            new FakeNavigationService(),
            new NoopExportService(),
            outcome);

        Assert.Equal("[故障日志] 我电脑卡了", viewModel.FactEvidenceDisplay[0]);
        Assert.Equal("旧历史事实", viewModel.FactEvidenceDisplay[1]);
    }

    [Fact]
    public async Task MarkdownExportUsesGroundedQuoteForNewFacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aigeektuner-grounding-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = await new MarkdownReportExportService(directory).ExportAsync(
                CreateOutcome(new DiagnosticEvidence
                {
                    Kind = EvidenceKind.Fact,
                    Description = "AI 声称用户说电箱烂了",
                    SourceId = DiagnosticEvidenceSourceIds.FaultLog,
                    SourceQuote = "我电脑卡了"
                }));
            var markdown = await File.ReadAllTextAsync(path);
            Assert.Contains("[故障日志] 我电脑卡了", markdown);
            Assert.DoesNotContain("电箱烂了", markdown);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static DiagnosisOutcome CreateOutcome(params DiagnosticEvidence[] evidence) => new()
    {
        Request = new DiagnosticRequest
        {
            RequestId = Guid.NewGuid(),
            Hardware = new HardwareInfo { CpuName = "Test CPU" },
            FaultLog = FaultLog.FromPastedText("我电脑卡了")
        },
        AiResult = new DiagnosticResult
        {
            Summary = "摘要",
            RootCause = "无法确定，需要进一步测试",
            Confidence = 0.2,
            RiskLevel = DiagnosticRiskLevel.Low,
            Evidence = evidence,
            Recommendations = []
        },
        Safety = new SafetyResult { Status = SafetyStatus.Approved }
    };

    private sealed class FakeNavigationService : INavigationService
    {
        public bool CanGoBack => false;
        public bool IsCurrent(AppPage page) => false;
        public void NavigateTo(AppPage page, object? parameter = null) { }
        public void GoBack() { }
    }

    private sealed class NoopExportService : IReportExportService
    {
        public Task<string> ExportAsync(DiagnosisOutcome outcome, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }
}
