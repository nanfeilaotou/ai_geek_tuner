using System.IO;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Settings;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Services.Voice;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.Services.Incidents
{
    /// <summary>
    /// Gate G（生产接线）：SessionsViewModel 停止录制后的 incident correlation。
    /// Recorder 走真实 TelemetryRecordingService（EmptyHub），
    /// Incident source 用 fake——不读真实事件日志、不 sleep。
    /// </summary>
    public sealed class SessionIncidentWiringTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public SessionIncidentWiringTests() { }

        public void Dispose() => _temp.Dispose();

        private sealed class EmptyHub : ITelemetryHub
        {
            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(TelemetrySnapshot.Empty(DateTimeOffset.UtcNow));
        }

        private sealed class FakeSettingsService : IApplicationSettingsService
        {
            public ApplicationSettings Current { get; private set; } = new();
            public void Load(ApplicationSettings settings) => Current = settings;
            public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class FakeAnalysisService : ISessionAnalysisService
        {
            public Task<SessionAnalysisRun> AnalyzeAsync(
                DiagnosticEvidenceContext context,
                CancellationToken cancellationToken) =>
                Task.FromResult(new SessionAnalysisRun(
                    Success: false, Result: null, ErrorMessage: "not used in this test",
                    ValidationErrors: [], RequestCount: 0, Duration: TimeSpan.Zero,
                    ModelName: "fake", RepairUsed: false));
        }

        private sealed class FakeVoiceService : IVoiceSynthesisService
        {
            public Task<VoiceSynthesisResult> SynthesizeAsync(
                string text, VoiceConfiguration configuration, CancellationToken cancellationToken) =>
                Task.FromResult(VoiceSynthesisResult.Fail("not used in this test"));
        }

        private sealed class FakeWavPlayback : IWavPlaybackService
        {
            public void PlayWav(byte[] wavBytes) { }
        }

        private SessionsViewModel CreateViewModel(
            ITelemetryRecordingService recorder, ISessionIncidentCorrelationService correlation)
        {
            return new SessionsViewModel(
                recorder,
                new TelemetrySessionStore(_temp.FullPath),
                new FakeSettingsService(),
                new FakeAnalysisService(),
                new SessionAnalysisStore(_temp.FullPath),
                new FakeVoiceService(),
                new FakeWavPlayback(),
                () => new VoiceConfiguration("http://localhost:9880", "", "", "", "zh", 1.0, null, null),
                correlation,
                new SessionIncidentStore(_temp.FullPath));
        }

        [Fact]
        public async Task StopAndAnalyze_SourceFailure_DoesNotBreakCompletedSession()
        {
            // Gate E：incident acquisition 失败绝不能让已完成的 Session 丢失。
            var store = new TelemetrySessionStore(_temp.FullPath);
            var recorder = new TelemetryRecordingService(new EmptyHub(), store);
            var correlation = new SessionIncidentCorrelationService(
                new ThrowingIncidentSource(),
                new SessionIncidentStore(_temp.FullPath));
            var viewModel = CreateViewModel(recorder, correlation);

            Assert.True(recorder.Start(200));
            await WaitFirstSampleAsync(recorder);

            await viewModel.StopAndAnalyzeAsync();

            var session = recorder.CurrentSession;
            Assert.NotNull(session);
            // Session 本体完好：session.json 已保存、摘要已构建、没有误报录制错误。
            Assert.True(File.Exists(store.PathOf(session!.Id)), "session.json must survive incident failure");
            Assert.True(viewModel.HasResult);
            Assert.False(viewModel.HasError);
            // 失败路径没有产生 incidents.json。
            Assert.False(File.Exists(Path.Combine(_temp.FullPath, session.Id, "incidents.json")));
        }

        [Fact]
        public async Task StopAndAnalyze_ProductionStyle_WritesSessionJson_AndIncidentsJson()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var incidentStore = new SessionIncidentStore(_temp.FullPath);
            var recorder = new TelemetryRecordingService(new EmptyHub(), store);
            var correlation = new SessionIncidentCorrelationService(
                new StubIncidentSource(), incidentStore);
            var viewModel = CreateViewModel(recorder, correlation);

            Assert.True(recorder.Start(200));
            await WaitFirstSampleAsync(recorder);

            await viewModel.StopAndAnalyzeAsync();

            var session = recorder.CurrentSession;
            Assert.NotNull(session);
            Assert.True(File.Exists(store.PathOf(session!.Id)), "session.json must exist");
            Assert.True(File.Exists(incidentStore.PathOf(session.Id)), "incidents.json must exist");

            var envelope = incidentStore.Load(session.Id);
            Assert.NotNull(envelope);
            Assert.Equal(session.Id, envelope!.SessionId);
            Assert.Equal(IncidentQueryStatus.Success, envelope.QueryStatus);
            // Gate F：EvidenceId 原样落盘，不重新编号。
            Assert.Equal("incident:0001", Assert.Single(envelope.Incidents).EvidenceId);
            // 窗口覆盖 session 边界 + 30s buffer。
            Assert.Equal(session.StartedAtUtc - TimeSpan.FromSeconds(30), envelope.WindowStartUtc);
            Assert.Equal(session.CompletedAtUtc!.Value + TimeSpan.FromSeconds(30), envelope.WindowEndUtc);
        }

        private static async Task WaitFirstSampleAsync(TelemetryRecordingService recorder)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (recorder.CurrentSession is null || recorder.CurrentSession.Samples.Count == 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("recorder did not capture a sample within 5s");
                }

                await Task.Delay(50);
            }
        }

        private sealed class ThrowingIncidentSource : IIncidentSource
        {
            public Task<IncidentQueryResult> QueryAsync(
                IncidentQuery query, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("event log unavailable");
        }

        private sealed class StubIncidentSource : IIncidentSource
        {
            public Task<IncidentQueryResult> QueryAsync(
                IncidentQuery query, CancellationToken cancellationToken = default) =>
                Task.FromResult<IncidentQueryResult>(new IncidentQueryResult(
                    new List<WindowsIncident>
                    {
                        new(
                            OccurredAtUtc: query.StartUtc.AddSeconds(31),
                            Category: IncidentCategory.UnexpectedShutdown,
                            Severity: IncidentSeverity.Critical,
                            ProviderName: "Microsoft-Windows-Kernel-Power",
                            EventId: 41,
                            Channel: "System",
                            RecordId: 42,
                            Summary: "系统未经正常关机就重新启动",
                            Details: null,
                            EvidenceId: "incident:0001"),
                    },
                    new List<IncidentChannelResult>
                    {
                        new("System", IncidentQueryStatus.Success, null, 1),
                        new("Application", IncidentQueryStatus.Success, null, 0),
                    }));
        }
    }
}
