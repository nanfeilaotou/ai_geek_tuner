using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Context;
using AIGeekTuner.Services.Dialogs;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.Files;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.History;
using AIGeekTuner.Services.Knowledge;
using AIGeekTuner.Services.Navigation;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Tests.ViewModels;

public sealed class DiagnosisOwnershipTests
{
    [Fact]
    public async Task RequestSnapshotsUserDescriptionBeforeFirstAwait_AndKeepsFaultLogSeparate()
    {
        var hardware = new GatedHardwareService();
        var diagnosis = new CapturingDiagnosisService();
        var navigation = new FakeNavigationService();
        var viewModel = CreateViewModel(navigation, hardware, diagnosis);
        navigation.CurrentDataContext = viewModel;
        viewModel.FaultLogText = "日志 B";
        viewModel.UserDescription = "问题 A";

        var running = viewModel.StartDiagnosisCommand.ExecuteAsync();
        await hardware.Started.Task;
        // 模拟用户在硬件采集 await 期间编辑输入；本次 request 不应改变。
        viewModel.FaultLogText = "日志 C";
        viewModel.UserDescription = "问题 C";
        hardware.Release();
        await running;

        Assert.NotNull(diagnosis.Request);
        Assert.Equal("日志 B", diagnosis.Request!.FaultLog.Content);
        Assert.Equal("问题 A", diagnosis.Request.UserDescription);
        Assert.DoesNotContain("[用户描述]", diagnosis.Request.FaultLog.Content);
    }

    [Fact]
    public async Task OldDiagnosisCompletionDoesNotNavigateOrOverwriteLatestForNewPage()
    {
        var navigation = new FakeNavigationService();
        var latest = new LatestDiagnosisState();
        var history = new NoopHistoryService();
        var first = new GatedDiagnosisService();
        var second = new GatedDiagnosisService();

        var vmA = CreateViewModel(navigation, new ImmediateHardwareService(), first, latest, history);
        navigation.CurrentDataContext = vmA;
        vmA.FaultLogText = "我电脑卡了";
        var runA = vmA.StartDiagnosisCommand.ExecuteAsync();
        await first.Started.Task;

        var vmB = CreateViewModel(navigation, new ImmediateHardwareService(), second, latest, history);
        navigation.CurrentDataContext = vmB;
        vmB.FaultLogText = "电池没电了";
        var runB = vmB.StartDiagnosisCommand.ExecuteAsync();
        await second.Started.Task;

        first.Release();
        await runA;
        Assert.Empty(navigation.ResultInputs);
        Assert.Null(latest.Outcome);

        second.Release();
        await runB;
        var result = Assert.Single(navigation.ResultInputs);
        Assert.Equal("电池没电了", result.Request.FaultLog.Content);
        Assert.Equal("电池没电了", latest.Outcome!.Request.FaultLog.Content);
    }

    [Fact]
    public async Task NewFileReplacesFaultLogButPreservesUserDescription_AndFreshVmIsEmpty()
    {
        var fresh = CreateViewModel(new FakeNavigationService(), new ImmediateHardwareService(), new CapturingDiagnosisService());
        Assert.Equal(string.Empty, fresh.FaultLogText);
        Assert.Equal(string.Empty, fresh.UserDescription);

        var vm = CreateViewModel(
            new FakeNavigationService(),
            new ImmediateHardwareService(),
            new CapturingDiagnosisService(),
            filePicker: new FixedFilePickerService("new.log"),
            fileReader: new FixedFileReaderService(FaultLog.FromPastedText("新日志")));
        vm.UserDescription = "问题 A";
        await vm.SelectFileCommand.ExecuteAsync();

        Assert.Equal("新日志", vm.FaultLogText);
        Assert.Equal("问题 A", vm.UserDescription);
    }

    [Fact]
    public async Task FailedDiagnosisKeepsBothInputsForRetry()
    {
        var vm = CreateViewModel(
            new FakeNavigationService(),
            new ImmediateHardwareService(),
            new FailingDiagnosisService());
        vm.FaultLogText = "日志 A";
        vm.UserDescription = "问题 A";

        await vm.StartDiagnosisCommand.ExecuteAsync();

        Assert.Equal("日志 A", vm.FaultLogText);
        Assert.Equal("问题 A", vm.UserDescription);
    }

    private static DiagnosisViewModel CreateViewModel(
        FakeNavigationService navigation,
        IHardwareDetectionService hardware,
        IDiagnosisService diagnosis,
        LatestDiagnosisState? latest = null,
        IDiagnosisHistoryService? history = null,
        IFilePickerService? filePicker = null,
        IFileReaderService? fileReader = null) => new(
        navigation,
        filePicker ?? new NoopFilePickerService(),
        fileReader ?? new NoopFileReaderService(),
        hardware,
        diagnosis,
        history ?? new NoopHistoryService(),
        new ImmediateSystemContextCollector(),
        new ImmediateKnowledgeService(),
        new FakeSettingsService(),
        new DiagnosticConfigurationStore(new DiagnosticConfiguration(
            new OllamaOptions { TimeoutSeconds = 30 },
            new DiagnosisInputOptions())),
        latest ?? new LatestDiagnosisState());

    private sealed class FakeNavigationService : INavigationService
    {
        public bool CanGoBack => false;
        public object? CurrentDataContext { get; set; }
        public List<DiagnosisOutcome> ResultInputs { get; } = [];
        public bool IsCurrent(AppPage page) => page == AppPage.Diagnosis;
        public bool IsCurrentDataContext(object dataContext) => ReferenceEquals(CurrentDataContext, dataContext);
        public void NavigateTo(AppPage page, object? parameter = null)
        {
            if (page == AppPage.Result && parameter is DiagnosisOutcome outcome)
            {
                ResultInputs.Add(outcome);
            }
        }
        public void GoBack() { }
    }

    private sealed class GatedHardwareService : IHardwareDetectionService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HardwareInfo> DetectAsync(CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HardwareInfo { CpuName = "Test CPU" };
        }
        public void Release() => _release.SetResult();
    }

    private sealed class ImmediateHardwareService : IHardwareDetectionService
    {
        public Task<HardwareInfo> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInfo { CpuName = "Test CPU" });
    }

    private sealed class CapturingDiagnosisService : IDiagnosisService
    {
        public DiagnosticRequest? Request { get; private set; }
        public Task<DiagnosisOutcome> DiagnoseAsync(DiagnosticRequest request, DiagnosticConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(CreateOutcome(request));
        }
    }

    private sealed class FailingDiagnosisService : IDiagnosisService
    {
        public Task<DiagnosisOutcome> DiagnoseAsync(DiagnosticRequest request, DiagnosticConfiguration configuration, CancellationToken cancellationToken = default) =>
            throw new DiagnosisException(DiagnosisError.AiRequestFailed, "测试失败");
    }

    private sealed class GatedDiagnosisService : IDiagnosisService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DiagnosticRequest? _request;
        public async Task<DiagnosisOutcome> DiagnoseAsync(DiagnosticRequest request, DiagnosticConfiguration configuration, CancellationToken cancellationToken = default)
        {
            _request = request;
            Started.SetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateOutcome(_request);
        }
        public void Release() => _release.SetResult();
    }

    private static DiagnosisOutcome CreateOutcome(DiagnosticRequest request) => new()
    {
        Request = request,
        AiResult = new DiagnosticResult
        {
            Summary = "摘要",
            RootCause = "无法确定，需要进一步测试",
            Confidence = 0.2,
            RiskLevel = DiagnosticRiskLevel.Low,
            Evidence = [],
            Recommendations = []
        },
        Safety = new SafetyResult { Status = SafetyStatus.Approved }
    };

    private sealed class ImmediateSystemContextCollector : ISystemContextCollector
    {
        public Task<SystemContext> CollectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemContext());
    }
    private sealed class ImmediateKnowledgeService : IDiagnosticKnowledgeService
    {
        public Task<IReadOnlyList<DiagnosticKnowledgeEntry>> FindMatchesAsync(FaultLog faultLog, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiagnosticKnowledgeEntry>>([]);
    }
    private sealed class FakeSettingsService : IApplicationSettingsService
    {
        public ApplicationSettings Current { get; } = new() { AutoSaveDiagnosisHistory = false };
        public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoopFilePickerService : IFilePickerService { public string? PickFaultLogFile() => null; }
    private sealed class NoopFileReaderService : IFileReaderService
    {
        public Task<FaultLog> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(FaultLog.FromPastedText(""));
    }
    private sealed class FixedFilePickerService(string path) : IFilePickerService
    {
        public string? PickFaultLogFile() => path;
    }
    private sealed class FixedFileReaderService(FaultLog faultLog) : IFileReaderService
    {
        public Task<FaultLog> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(faultLog);
    }
    private sealed class NoopHistoryService : IDiagnosisHistoryService
    {
        public Task<DiagnosisRecord> SaveSuccessAsync(DiagnosisOutcome outcome, string modelName, long durationMs, CancellationToken cancellationToken, string? providerName = null) => throw new NotSupportedException();
        public Task<DiagnosisRecord> SaveFailureAsync(DiagnosisFailureInfo failure, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiagnosisRecord>> GetRecordsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DiagnosisRecord>>([]);
        public Task<DiagnosisHistoryDetail> LoadDetailAsync(DiagnosisRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Guid diagnosisId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
