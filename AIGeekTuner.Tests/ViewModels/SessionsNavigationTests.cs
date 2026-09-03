using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using AIGeekTuner.Tests.Services.Incidents;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels
{
    /// <summary>
    /// V2-M4.5E：SessionsPage 三状态 UX（Record / History / Detail）导航回归（Gate J）。
    /// 只测 presentation 状态机与 recorder 边界——不触碰真实事件日志、不调真实 AI/语音。
    /// </summary>
    public sealed class SessionsNavigationTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private static readonly DateTimeOffset T0 =
            new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        private sealed class EmptyHub : ITelemetryHub
        {
            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(TelemetrySnapshot.Empty(DateTimeOffset.UtcNow));
        }

        private sealed class FakeSettingsService : IApplicationSettingsService
        {
            public ApplicationSettings Current { get; set; } = new();
            public void Load(ApplicationSettings settings) => Current = settings;
            public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class FakeAnalysisService : ISessionAnalysisService
        {
            public Task<SessionAnalysisRun> AnalyzeAsync(DiagnosticEvidenceContext context, CancellationToken cancellationToken) =>
                Task.FromResult(new SessionAnalysisRun(
                    false, null, "not used", [], 0, TimeSpan.Zero, "fake", false));
        }

        /// <summary>Gate J(14)：统计真实 AI 调用次数——浏览 Detail 绝不允许触发重分析。</summary>
        private sealed class CountingAnalysisService : ISessionAnalysisService
        {
            public int Calls { get; private set; }

            public Task<SessionAnalysisRun> AnalyzeAsync(DiagnosticEvidenceContext context, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new SessionAnalysisRun(
                    true,
                    new SessionAnalysisResult(
                        "总体正常",
                        SessionOverallAssessment.Normal,
                        0.9,
                        Array.Empty<SessionFinding>(),
                        Array.Empty<SessionRecommendation>(),
                        Array.Empty<string>(),
                        "口语化摘要，长度必须足够满足校验规则。"),
                    null,
                    Array.Empty<string>(),
                    1,
                    TimeSpan.FromSeconds(1),
                    "test-model",
                    false,
                    "{}"));
            }
        }

        private sealed class FakeVoiceService : IVoiceSynthesisService
        {
            public Task<VoiceSynthesisResult> SynthesizeAsync(
                string text, VoiceConfiguration configuration, CancellationToken cancellationToken) =>
                Task.FromResult(VoiceSynthesisResult.Fail("not used"));
        }

        private sealed class FakeWavPlayback : IWavPlaybackService
        {
            public void PlayWav(byte[] wavBytes) { }
        }

        /// <summary>M4.5E.3：记录播放请求字节的 fake——缓存 → 播放的逐字节契约。</summary>
        private sealed class RecordingWavPlayback : IWavPlaybackService
        {
            public int Calls;
            public List<byte[]> Received { get; } = new();

            /// <summary>PlayWav 首次调用瞬间的快照回调（记录上下文路由计数）。</summary>
            public Func<int>? OnFirstPlaySnapshot;

            public int PostCountAtFirstPlay { get; private set; } = -1;

            public void PlayWav(byte[] wavBytes)
            {
                if (Calls == 0)
                {
                    PostCountAtFirstPlay = OnFirstPlaySnapshot?.Invoke() ?? -1;
                }

                Calls++;
                Received.Add((byte[])wavBytes.Clone());
            }
        }

        /// <summary>M4.5E.3：可门控的 fake——PlayWav 阻塞模拟真实播放时长（PlaySync 语义）。</summary>
        private sealed class GatedWavPlayback : IWavPlaybackService
        {
            private readonly ManualResetEventSlim _gate = new(false);

            public int Calls;
            public List<byte[]> Received { get; } = new();

            public void Release() => _gate.Set();

            public void PlayWav(byte[] wavBytes)
            {
                Calls++;
                Received.Add((byte[])wavBytes.Clone());
                _gate.Wait(TimeSpan.FromSeconds(10));   // 播放期间占用后台线程
            }
        }

        /// <summary>M4.5E.3 Gate E：记录 Post 的测试同步上下文（内联执行保持确定性）。</summary>
        private sealed class RecordingSynchronizationContext : SynchronizationContext
        {
            public int PostCount;

            public override void Post(SendOrPostCallback d, object? state)
            {
                PostCount++;
                d(state);
            }
        }

        /// <summary>M4.5E.3：总是失败的语音服务——播放失败可见性测试用。</summary>
        private sealed class FailingVoiceService : IVoiceSynthesisService
        {
            public Task<VoiceSynthesisResult> SynthesizeAsync(
                string text, VoiceConfiguration configuration, CancellationToken cancellationToken) =>
                Task.FromResult(VoiceSynthesisResult.Fail("synthesis unavailable"));
        }

        /// <summary>Gate J(15)：统计 incidents 采集次数——浏览 Detail 绝不允许重新查询事件日志。</summary>
        private sealed class CountingIncidentCorrelation : ISessionIncidentCorrelationService
        {
            public int Captures { get; private set; }

            public List<string> CapturedIds { get; } = new();

            public Task<SessionIncidentEnvelope> CaptureAsync(
                TelemetryRecordingSession session, CancellationToken cancellationToken = default)
            {
                Captures++;
                CapturedIds.Add(session.Id);
                return Task.FromResult(SessionIncidentStoreTests.MakeEnvelope(session.Id));
            }
        }

        /// <summary>M4.5E.2：可门控的分析服务——模拟长耗时 AI，测试中手动 Complete。</summary>
        private sealed class GatedAnalysisService : ISessionAnalysisService
        {
            private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Calls { get; private set; }

            public void Complete() => _tcs.TrySetResult();

            public async Task<SessionAnalysisRun> AnalyzeAsync(DiagnosticEvidenceContext context, CancellationToken cancellationToken)
            {
                Calls++;
                await _tcs.Task.ConfigureAwait(false);
                return MakeRun();
            }

            internal static SessionAnalysisRun MakeRun() => new(
                true,
                new SessionAnalysisResult(
                    "总体正常",
                    SessionOverallAssessment.Normal,
                    0.9,
                    Array.Empty<SessionFinding>(),
                    Array.Empty<SessionRecommendation>(),
                    Array.Empty<string>(),
                    "口语化摘要，长度必须足够满足校验规则。"),
                null,
                Array.Empty<string>(),
                1,
                TimeSpan.FromSeconds(1),
                "test-model",
                false,
                "{}");
        }

        /// <summary>M4.5E.2：可门控的语音服务——模拟长耗时 GSV 合成。</summary>
        private sealed class GatedVoiceService : IVoiceSynthesisService
        {
            private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Calls { get; private set; }

            public void Complete() => _tcs.TrySetResult();

            public async Task<VoiceSynthesisResult> SynthesizeAsync(
                string text, VoiceConfiguration configuration, CancellationToken cancellationToken)
            {
                Calls++;
                await _tcs.Task.ConfigureAwait(false);
                return VoiceSynthesisResult.Ok(TestWav.Create());
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(condition(), "condition not met within timeout");
        }

        private (SessionsViewModel Vm, TelemetryRecordingService Recorder) Create(
            ISessionAnalysisService? analysis = null,
            ISessionIncidentCorrelationService? correlation = null,
            int intervalMs = 200,
            string voiceEndpoint = "http://localhost:9880",
            IVoiceSynthesisService? voice = null,
            IWavPlaybackService? playback = null)
        {
            var settings = new FakeSettingsService();
            settings.Current = new ApplicationSettings { RecordingIntervalMs = intervalMs };
            var store = new TelemetrySessionStore(_temp.FullPath);
            var recorder = new TelemetryRecordingService(new EmptyHub(), store);
            var viewModel = new SessionsViewModel(
                recorder,
                store,
                settings,
                analysis ?? new FakeAnalysisService(),
                new SessionAnalysisStore(_temp.FullPath),
                voice ?? new FakeVoiceService(),
                playback ?? new FakeWavPlayback(),
                () => new VoiceConfiguration(voiceEndpoint, "", "", "", "zh", 1.0, null, null),
                correlation ?? new CountingIncidentCorrelation(),
                new SessionIncidentStore(_temp.FullPath));
            return (viewModel, recorder);
        }

        private static void SaveSession(TelemetrySessionStore store, string id)
        {
            var session = TelemetryRecordingSession.Start(1000, T0) with
            {
                Id = id,
                Status = RecordingStatus.Completed,
                CompletedAtUtc = T0.AddSeconds(30),
            };
            store.Save(session);
        }

        private void EnterHistory(SessionsViewModel viewModel) =>
            viewModel.OpenHistoryViewCommand.Execute(null);

        private void OpenDetail(SessionsViewModel viewModel, string sessionId)
        {
            EnterHistory(viewModel);
            viewModel.SelectedRecent =
                viewModel.RecentSessions.Single(item => item.Id == sessionId);
            viewModel.SelectRecentCommand.Execute(null);
        }

        // ---- Gate J(1)：fresh page → Record ----
        [Fact]
        public void FreshPage_DefaultsToRecordMode()
        {
            var (viewModel, _) = Create();

            Assert.Equal(SessionsViewModel.SessionPageMode.Record, viewModel.PageMode);
            Assert.True(viewModel.IsRecordMode);
            Assert.False(viewModel.IsRecording);
            Assert.True(viewModel.HasEmptyState);   // 开始录制入口可见
        }

        // ---- Gate J(2)：History 在零录制下即可进入 ----
        [Fact]
        public void HistoryAccessible_WithZeroRecordings()
        {
            var (viewModel, recorder) = Create();

            EnterHistory(viewModel);

            Assert.Equal(SessionsViewModel.SessionPageMode.History, viewModel.PageMode);
            Assert.True(viewModel.IsHistoryMode);
            Assert.False(recorder.IsRecording);     // 只是浏览，没有副作用
        }

        // ---- Gate J(3)：空历史 → 暂无录制历史 ----
        [Fact]
        public void EmptyHistory_ShowsEmptyStateText()
        {
            var (viewModel, _) = Create();

            EnterHistory(viewModel);

            Assert.True(viewModel.HasNoHistory);
            Assert.Empty(viewModel.RecentSessions);
        }

        // ---- Gate J(4)：选中历史 → Detail ----
        [Fact]
        public void SelectHistoryEntry_OpensDetail()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            var (viewModel, _) = Create();

            OpenDetail(viewModel, "s-a");

            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
            Assert.True(viewModel.IsDetailMode);
            Assert.True(viewModel.HasResult);
            Assert.Equal("s-a", viewModel.SelectedRecent!.Id);
            Assert.Equal("s-a", viewModel.CurrentDetailSessionId);   // Gate B：Detail 会话事实源
        }

        // ---- Gate J(5)：Detail → History ----
        [Fact]
        public void Detail_BackToHistory()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            var (viewModel, _) = Create();
            OpenDetail(viewModel, "s-a");

            EnterHistory(viewModel);

            Assert.Equal(SessionsViewModel.SessionPageMode.History, viewModel.PageMode);
        }

        // ---- Gate J(6)：Detail → Record（未录制 → 显示开始入口） ----
        [Fact]
        public void Detail_ToRecord_ShowsStartEntry_NotRecording()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            var (viewModel, recorder) = Create();
            OpenDetail(viewModel, "s-a");

            viewModel.OpenRecordViewCommand.Execute(null);

            Assert.Equal(SessionsViewModel.SessionPageMode.Record, viewModel.PageMode);
            Assert.False(viewModel.IsRecording);
            Assert.False(recorder.IsRecording);
            Assert.True(viewModel.HasEmptyState);           // 开始录制入口恢复可见
            Assert.Equal("新建录制", viewModel.NewRecordingButtonText);
        }

        // ---- Gate J(7)：Stop → Detail（保持既有自动进入，Gate G） ----
        [Fact]
        public async Task Stop_Finalizes_EntersDetail()
        {
            var (viewModel, _) = Create(intervalMs: 200);
            viewModel.StartRecordingCommand.Execute(null);
            Assert.True(viewModel.IsRecording);
            await Task.Delay(700);
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
            Assert.True(viewModel.HasResult);
            Assert.False(viewModel.IsRecording);
            Assert.NotNull(viewModel.CurrentDetailSessionId);   // M4.5E.1：Detail = 刚完成的会话
        }

        // ---- Gate J(8)：录制中浏览 History，recorder 不停 ----
        [Fact]
        public async Task BrowsingHistoryDuringRecording_KeepsRecorderRunning()
        {
            var (viewModel, recorder) = Create(intervalMs: 200);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var samplesBefore = recorder.CurrentSession!.Samples.Count;

            EnterHistory(viewModel);

            await Task.Delay(500);   // 停留在历史视图期间采样继续
            Assert.Equal(SessionsViewModel.SessionPageMode.History, viewModel.PageMode);
            Assert.True(viewModel.IsRecording);
            Assert.True(recorder.IsRecording);
            Assert.True(recorder.CurrentSession!.Samples.Count > samplesBefore);
        }

        // ---- Gate J(9)：History → 返回当前录制 ----
        [Fact]
        public async Task FromHistory_ReturnToActiveRecording()
        {
            var (viewModel, recorder) = Create(intervalMs: 200);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(300);
            EnterHistory(viewModel);
            var sessionId = recorder.CurrentSession!.Id;

            viewModel.OpenRecordViewCommand.Execute(null);

            Assert.Equal(SessionsViewModel.SessionPageMode.Record, viewModel.PageMode);
            Assert.True(viewModel.IsRecording);
            Assert.True(recorder.IsRecording);
            Assert.Equal(sessionId, recorder.CurrentSession!.Id);   // 同一会话继续，未被重启
        }

        // ---- Gate J(10)：禁止第二个录制 ----
        [Fact]
        public async Task SecondRecording_Prevented()
        {
            var (viewModel, recorder) = Create(intervalMs: 200);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(300);
            var sessionId = recorder.CurrentSession!.Id;

            // Gate I：录制中 Start 命令禁用；“新建录制/重新录制”文案变为“返回当前录制”。
            Assert.False(viewModel.StartRecordingCommand.CanExecute(null));
            Assert.Equal("返回当前录制", viewModel.NewRecordingButtonText);

            // 即便直接调用 StartRecording，recorder 单会话保护也会拒绝，原会话不动。
            viewModel.StartRecording();
            Assert.True(recorder.IsRecording);
            Assert.Equal(sessionId, recorder.CurrentSession!.Id);
        }

        // ---- Gate J(11)：选中会话在视图切换间保持 ----
        [Fact]
        public void SelectedSession_Persists_AcrossViewSwitches()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var (viewModel, _) = Create();

            OpenDetail(viewModel, "s-b");
            EnterHistory(viewModel);

            // 回到 History 后选择仍在；再次进入 Detail 仍是 s-b。
            Assert.NotNull(viewModel.SelectedRecent);
            Assert.Equal("s-b", viewModel.SelectedRecent!.Id);
            viewModel.SelectRecentCommand.Execute(null);
            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
            Assert.Equal("s-b", viewModel.SelectedRecent!.Id);
        }

        // ---- Gate J(12)：History 中删除 ----
        [Fact]
        public void DeleteFromHistory_RemovesEntry_StaysInHistory()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var (viewModel, _) = Create();
            EnterHistory(viewModel);
            viewModel.SelectedRecent = viewModel.RecentSessions.Single(item => item.Id == "s-a");
            viewModel.ConfirmDelete = _ => true;

            viewModel.DeleteRecentCommand.Execute(null);

            Assert.DoesNotContain(viewModel.RecentSessions, item => item.Id == "s-a");
            Assert.Single(viewModel.RecentSessions);
            Assert.Equal(SessionsViewModel.SessionPageMode.History, viewModel.PageMode);
        }

        // ---- Gate J(12b)：删除当前 Detail 载入的会话 → Detail 状态一并失效 ----
        [Fact]
        public void DeleteLoadedSession_InvalidatesDetailState()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var (viewModel, _) = Create();
            OpenDetail(viewModel, "s-a");
            Assert.True(viewModel.HasResult);

            EnterHistory(viewModel);
            viewModel.SelectedRecent = viewModel.RecentSessions.Single(item => item.Id == "s-a");
            viewModel.ConfirmDelete = _ => true;
            viewModel.DeleteRecentCommand.Execute(null);

            Assert.False(viewModel.HasResult);
            Assert.Null(viewModel.SelectedRecent);
        }

        // ---- Gate J(13)：切页离开再回来：录制中 → 默认 Record 视图（Gate H） ----
        [Fact]
        public async Task NavigationAwayAndBack_WhileRecording_LandsOnRecordView()
        {
            var (viewModel, recorder) = Create(intervalMs: 200);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(300);
            EnterHistory(viewModel);   // 用户录制中浏览了历史
            Assert.Equal(SessionsViewModel.SessionPageMode.History, viewModel.PageMode);

            // 模拟切到 Hardware 再回 Sessions（页面重建 → Loaded → OnPageEntered）。
            viewModel.OnPageEntered();

            Assert.Equal(SessionsViewModel.SessionPageMode.Record, viewModel.PageMode);
            Assert.True(viewModel.IsRecording);
            Assert.True(recorder.IsRecording);
        }

        // ---- Gate J(13b)：未录制回来 → 保留最近内部模式（Gate H：保留模式方案） ----
        [Fact]
        public void NavigationAwayAndBack_NotRecording_KeepsMode()
        {
            var (viewModel, _) = Create();
            EnterHistory(viewModel);

            viewModel.OnPageEntered();

            Assert.Equal(SessionsViewModel.SessionPageMode.History, viewModel.PageMode);
        }

        // ---- Gate J(14)：浏览 Detail 不重跑 AI ----
        [Fact]
        public async Task BrowsingDetail_DoesNotRerunAi()
        {
            var analysis = new CountingAnalysisService();
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-ai");
            var (viewModel, _) = Create(analysis: analysis);

            // 进入 Detail 两次：SelectRecent 只恢复 analysis.json，绝不调用分析服务。
            OpenDetail(viewModel, "s-ai");
            EnterHistory(viewModel);
            OpenDetail(viewModel, "s-ai");
            Assert.Equal(0, analysis.Calls);

            // 显式分析一次 → 恰好一次调用并落盘。
            Assert.True(viewModel.AnalyzeCommand.CanExecute(null), "canExecute");
            await viewModel.AnalyzeCommand.ExecuteAsync();
            Assert.True(string.IsNullOrEmpty(viewModel.AnalysisError), "AnalysisError=" + viewModel.AnalysisError);
            Assert.True(viewModel.HasAnalysis, $"HasAnalysis=false sel={viewModel.SelectedRecent?.Id} analyzing={viewModel.IsAnalyzing}");
            Assert.Equal(1, analysis.Calls);

            // 再次离开/回到 Detail：从磁盘恢复，不重跑。
            EnterHistory(viewModel);
            OpenDetail(viewModel, "s-ai");
            Assert.Equal(1, analysis.Calls);
            Assert.True(viewModel.HasAnalysis);
        }

        // ---- Gate J(15)：浏览 Detail 不重新采集 incidents ----
        [Fact]
        public void BrowsingDetail_DoesNotReacquireIncidents()
        {
            var correlation = new CountingIncidentCorrelation();
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-inc");
            var (viewModel, _) = Create(correlation: correlation);

            OpenDetail(viewModel, "s-inc");
            EnterHistory(viewModel);
            OpenDetail(viewModel, "s-inc");

            Assert.Equal(0, correlation.Captures);   // 只读 incidents.json，从不 CaptureAsync
        }

        // ==================== M4.5E.1 Gate D：Detail 状态正确性 ====================

        private static SessionAnalysisResult MakeAnalysisResult() => new(
            Summary: "结合遥测的确定性说明。",
            OverallAssessment: SessionOverallAssessment.Normal,
            Confidence: 0.9,
            Findings: Array.Empty<SessionFinding>(),
            Recommendations: Array.Empty<SessionRecommendation>(),
            Uncertainties: Array.Empty<string>(),
            SpokenSummary: "口语化摘要，长度必须足够满足校验规则。");

        /// <summary>Gate D(1)+(2)：看过旧 A → 录 B → stop → Detail 必须是 B，且 History 选择仍停在 A。</summary>
        [Fact]
        public async Task HistoryA_Viewed_RecordB_Stop_ShowsDetailB_SelectionStaysA()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            var (viewModel, recorder) = Create();
            OpenDetail(viewModel, "s-a-old");
            Assert.Equal("s-a-old", viewModel.CurrentDetailSessionId);

            viewModel.OpenRecordViewCommand.Execute(null);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var bId = recorder.CurrentSession!.Id;
            Assert.NotEqual("s-a-old", bId);
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            // 核心断言用 SessionId，不比较 Summary 文本。
            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
            Assert.Equal(bId, viewModel.CurrentDetailSessionId);        // Detail = B
            Assert.Equal("s-a-old", viewModel.SelectedRecent!.Id);      // History 选择保持 A（M4.5E.1 Gate 2）
            Assert.True(viewModel.HasResult);
        }

        /// <summary>Gate D(2)续：stop 后来回切视图，Detail 会话仍是 B、不会漂回 A。</summary>
        [Fact]
        public async Task AfterStop_SwitchingViews_DetailStaysB()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            var (viewModel, recorder) = Create();
            OpenDetail(viewModel, "s-a-old");
            viewModel.OpenRecordViewCommand.Execute(null);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var bId = recorder.CurrentSession!.Id;
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            EnterHistory(viewModel);
            Assert.Equal("s-a-old", viewModel.SelectedRecent!.Id);      // 列表选择未被动过
            viewModel.OpenRecordViewCommand.Execute(null);              // 回 Record 再回 Detail 状态
            Assert.Equal(bId, viewModel.CurrentDetailSessionId);        // Detail 仍是 B
        }

        /// <summary>Gate D(3)：没有任何历史 → 录 B → stop → Detail B。</summary>
        [Fact]
        public async Task NoHistory_RecordB_Stop_ShowsDetailB()
        {
            var (viewModel, recorder) = Create();
            Assert.True(viewModel.HasNoHistory);

            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var bId = recorder.CurrentSession!.Id;
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            Assert.Equal(bId, viewModel.CurrentDetailSessionId);
            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
        }

        /// <summary>Gate D(4)：录制 B 期间浏览旧 A 详情 → stop → Detail B（不受浏览影响）。</summary>
        [Fact]
        public async Task ViewingAWhileRecordingB_Stop_ShowsDetailB()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            var (viewModel, recorder) = Create();

            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(300);
            OpenDetail(viewModel, "s-a-old");          // 录制中浏览 A（Gate I 允许）
            Assert.Equal("s-a-old", viewModel.CurrentDetailSessionId);

            var bId = recorder.CurrentSession!.Id;
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            Assert.Equal(bId, viewModel.CurrentDetailSessionId);
            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
        }

        /// <summary>Gate D(5)：Detail B → History → 选 A → Detail A（正向切换不受 stop 修复影响）。</summary>
        [Fact]
        public async Task DetailB_History_SelectA_DetailA()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            var (viewModel, recorder) = Create();
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var bId = recorder.CurrentSession!.Id;
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();
            Assert.Equal(bId, viewModel.CurrentDetailSessionId);

            EnterHistory(viewModel);
            viewModel.SelectedRecent = viewModel.RecentSessions.Single(item => item.Id == "s-a-old");
            viewModel.SelectRecentCommand.Execute(null);

            Assert.Equal("s-a-old", viewModel.CurrentDetailSessionId);   // 用户主动切换 → Detail A
            Assert.Equal(SessionsViewModel.SessionPageMode.Detail, viewModel.PageMode);
        }

        /// <summary>Gate D(6)：Detail A → 新建录制 → Record 视图（不自动开始采样）。</summary>
        [Fact]
        public void DetailA_NewRecording_GoesToRecordMode_NotAutoStart()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            var (viewModel, recorder) = Create();
            OpenDetail(viewModel, "s-a-old");

            viewModel.OpenRecordViewCommand.Execute(null);

            Assert.Equal(SessionsViewModel.SessionPageMode.Record, viewModel.PageMode);
            Assert.False(recorder.IsRecording);
            Assert.True(viewModel.HasEmptyState);      // 用户需自行点击“开始录制”
        }

        /// <summary>Gate D(7)+(8)：页头两个命令是唯一模式切换入口，Detail 无重复动作命令/属性。</summary>
        [Fact]
        public void PageHeader_IsOnlyNavigation_NoDuplicateDetailActions()
        {
            var type = typeof(SessionsViewModel);
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

            // 存在且仅存在的模式切换命令：页头两个。
            Assert.NotNull(type.GetProperty("OpenRecordViewCommand", flags));
            Assert.NotNull(type.GetProperty("OpenHistoryViewCommand", flags));

            // Problem A 反引入锁：不允许再出现 Detail 专属导航命令/文案属性。
            Assert.Null(type.GetProperty("BackToHistoryCommand", flags));
            Assert.Null(type.GetProperty("ReRecordCommand", flags));
            Assert.Null(type.GetProperty("ReRecordButtonText", flags));
            Assert.Null(type.GetProperty("ViewDetailCommand", flags));
        }

        /// <summary>Gate D(9)：stop 绝不重跑旧 A 的 AI 分析（B 尚无 analysis.json，AI 区必须重置为空）。</summary>
        [Fact]
        public async Task Stop_DoesNotRerunOldA_Analysis()
        {
            var analysis = new CountingAnalysisService();
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a-old", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            var (viewModel, recorder) = Create(analysis: analysis);

            OpenDetail(viewModel, "s-a-old");              // 从 analysis.json 恢复，不调服务
            Assert.Equal(0, analysis.Calls);
            Assert.True(viewModel.HasAnalysis);             // A 的旧分析在展示

            viewModel.OpenRecordViewCommand.Execute(null);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var bId = recorder.CurrentSession!.Id;
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            Assert.Equal(0, analysis.Calls);                // stop 全程零 AI 调用
            Assert.Equal(bId, viewModel.CurrentDetailSessionId);
            Assert.False(viewModel.HasAnalysis);            // B 无分析 → AI 区重置，A 的结果绝不残留
        }

        /// <summary>Gate D(10)：stop 只为新 B 采集一次 incidents，绝不为旧 A 重新采集。</summary>
        [Fact]
        public async Task Stop_DoesNotReacquireIncidents_ForOldA()
        {
            var correlation = new CountingIncidentCorrelation();
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a-old");
            var (viewModel, recorder) = Create(correlation: correlation);
            OpenDetail(viewModel, "s-a-old");

            viewModel.OpenRecordViewCommand.Execute(null);
            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(500);
            var bId = recorder.CurrentSession!.Id;
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            Assert.Equal(1, correlation.Captures);
            Assert.Equal([bId], correlation.CapturedIds);   // 采集对象就是刚完成的 B
        }

        /// <summary>M4.5E.1 补充：历史行“已分析/未分析”状态来自 analysis.json 存在性。</summary>
        [Fact]
        public void HistoryRows_MarkAnalysisState()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-done");
            SaveSession(store, "s-raw");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-done", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            var (viewModel, _) = Create();

            viewModel.OpenHistoryViewCommand.Execute(null);

            Assert.True(viewModel.RecentSessions.Single(item => item.Id == "s-done").IsAnalyzed);
            Assert.Equal("已分析", viewModel.RecentSessions.Single(item => item.Id == "s-done").AnalysisStateText);
            Assert.False(viewModel.RecentSessions.Single(item => item.Id == "s-raw").IsAnalyzed);
            Assert.Equal("未分析", viewModel.RecentSessions.Single(item => item.Id == "s-raw").AnalysisStateText);
        }

        // ==================== M4.5E.2 Gate K：AI per-session 状态 ====================

        /// <summary>Gate K(1)：A 分析中切到 B → B 不显示“正在分析”。</summary>
        [Fact]
        public async Task AnalyzeA_SwitchB_BNotAnalyzing()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var analysis = new GatedAnalysisService();
            var (viewModel, _) = Create(analysis: analysis);
            OpenDetail(viewModel, "s-a");

            var analyzeTask = viewModel.AnalyzeCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsAnalyzing);
            Assert.True(viewModel.IsCurrentDetailAnalyzing);        // A 上可见

            OpenDetail(viewModel, "s-b");
            Assert.False(viewModel.IsCurrentDetailAnalyzing);       // B 不显示 A 的分析中
            Assert.False(viewModel.HasAnalysis);                    // B 从未分析 → 显示未分析

            analysis.Complete();
            await analyzeTask;
        }

        /// <summary>Gate K(2)+(3)：A 完成时用户在 B 上——B 展示不变；A 的 analysis.json 必然落盘。</summary>
        [Fact]
        public async Task AnalyzeA_SwitchB_ACompletes_BResultUnchanged_PersistsA()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var analysis = new GatedAnalysisService();
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            var (viewModel, _) = Create(analysis: analysis);
            OpenDetail(viewModel, "s-a");

            var analyzeTask = viewModel.AnalyzeCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsAnalyzing);
            OpenDetail(viewModel, "s-b");
            Assert.False(viewModel.HasAnalysis);

            analysis.Complete();
            await analyzeTask;

            Assert.Equal("s-b", viewModel.CurrentDetailSessionId);
            Assert.False(viewModel.HasAnalysis);                    // B 的 Detail 未被 A 的结果污染
            Assert.False(viewModel.IsCurrentDetailAnalyzing);
            // Gate K(3)：A 的分析已持久化到 analysis.json（文件事实，与界面无关）。
            Assert.NotNull(analysisStore.Load("s-a"));
        }

        /// <summary>Gate K(4)：A 分析中切走再切回 → A 显示“分析中”；完成 → A 结果渲染。</summary>
        [Fact]
        public async Task SwitchBackA_DuringRun_AnalyzingVisible_ThenResultRendered()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var analysis = new GatedAnalysisService();
            var (viewModel, _) = Create(analysis: analysis);
            OpenDetail(viewModel, "s-a");

            var analyzeTask = viewModel.AnalyzeCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsAnalyzing);
            OpenDetail(viewModel, "s-b");
            OpenDetail(viewModel, "s-a");

            Assert.Equal("s-a", viewModel.CurrentDetailSessionId);
            Assert.True(viewModel.IsCurrentDetailAnalyzing);        // 切回 A：分析中可见
            Assert.False(viewModel.HasAnalysis);                    // 尚无结果

            analysis.Complete();
            await analyzeTask;

            Assert.True(viewModel.HasAnalysis);                     // 完成后 A 结果渲染
            Assert.Equal("总体正常", viewModel.AnalysisSummary);
            Assert.False(viewModel.IsCurrentDetailAnalyzing);
        }

        /// <summary>Gate K(5)：A 完成后才切回 → 经 analysis.json 恢复显示 A 结果（非实时渲染路径）。</summary>
        [Fact]
        public async Task SwitchBackA_AfterCompletion_RestoredFromDisk()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var analysis = new GatedAnalysisService();
            var (viewModel, _) = Create(analysis: analysis);
            OpenDetail(viewModel, "s-a");

            var analyzeTask = viewModel.AnalyzeCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsAnalyzing);
            OpenDetail(viewModel, "s-b");
            analysis.Complete();
            await analyzeTask;

            OpenDetail(viewModel, "s-a");
            Assert.True(viewModel.HasAnalysis);                     // analysis.json restore
            Assert.Equal("总体正常", viewModel.AnalysisSummary);
        }

        /// <summary>Gate K(6)：B 已分析过 → 从 A 的运行中切到 B，B 显示自己的旧结果，不受 A 影响。</summary>
        [Fact]
        public async Task BPreAnalyzed_SwitchFromRunningA_BOwnResultRemains()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-b", T0, "old-model", 50, false, MakeAnalysisResult(), "{}"));
            var analysis = new GatedAnalysisService();
            var (viewModel, _) = Create(analysis: analysis);
            OpenDetail(viewModel, "s-a");

            var analyzeTask = viewModel.AnalyzeCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsAnalyzing);
            OpenDetail(viewModel, "s-b");

            Assert.True(viewModel.HasAnalysis);                     // B 自己的结果
            Assert.Equal("结合遥测的确定性说明。", viewModel.AnalysisSummary);   // B 的旧 summary，不是 A 的
            Assert.False(viewModel.IsCurrentDetailAnalyzing);

            analysis.Complete();
            await analyzeTask;
            Assert.Equal("结合遥测的确定性说明。", viewModel.AnalysisSummary);   // A 完成也不改写 B
        }

        /// <summary>Gate K(7)：A 完成时用户在 B 上 → 历史列表 A 的“已分析”标记照样更新。</summary>
        [Fact]
        public async Task HistoryMarker_Updates_AfterCompletion_EvenIfDetailIsB()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            var analysis = new GatedAnalysisService();
            var (viewModel, _) = Create(analysis: analysis);
            OpenDetail(viewModel, "s-a");

            var analyzeTask = viewModel.AnalyzeCommand.ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsAnalyzing);
            OpenDetail(viewModel, "s-b");
            Assert.False(viewModel.RecentSessions.Single(item => item.Id == "s-a").IsAnalyzed);

            analysis.Complete();
            await analyzeTask;

            Assert.True(viewModel.RecentSessions.Single(item => item.Id == "s-a").IsAnalyzed);   // Gate D ② LoadRecent
        }

        // ==================== M4.5E.2 Gate K：Voice 缓存恢复 / per-session ====================

        /// <summary>Gate K(8)+(16)：缓存已在磁盘 → 全新 ViewModel（重启等价）第一次 ShowDetail 即显示“已生成”，零合成调用。</summary>
        [Fact]
        public void CachedWav_ColdStart_ShowDetail_ShowsGenerated_WithoutAnySynthesize()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            analysisStore.SaveVoiceWav("s-a", TestWav.Create());
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voiceEndpoint: "", voice: voice);   // 空 endpoint：确保没有任何预生成路径

            OpenDetail(viewModel, "s-a");

            Assert.Equal(0, voice.Calls);                           // 没有任何 Synthesize 调用
            Assert.Equal("语音已生成 · 播放将直接使用缓存", viewModel.VoiceStateText);
        }

        /// <summary>Gate K(9)：已分析但无缓存 → 未生成（不猜“分析过就一定有语音”）。</summary>
        [Fact]
        public void Analyzed_NoWav_ShowsNotGenerated()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            var (viewModel, _) = Create(voiceEndpoint: "");

            OpenDetail(viewModel, "s-a");

            Assert.True(viewModel.HasAnalysis);
            Assert.Equal("未生成", viewModel.VoiceStateText);
        }

        /// <summary>Gate K(10)：A 的缓存绝不让 B 显示“已生成”。</summary>
        [Fact]
        public void ACache_DoesNotMakeBGenerated()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-b", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            analysisStore.SaveVoiceWav("s-a", TestWav.Create());
            var (viewModel, _) = Create(voiceEndpoint: "");

            OpenDetail(viewModel, "s-a");
            Assert.Equal("语音已生成 · 播放将直接使用缓存", viewModel.VoiceStateText);

            OpenDetail(viewModel, "s-b");
            Assert.Equal("未生成", viewModel.VoiceStateText);
        }

        /// <summary>Gate K(12)：A 生成语音中切 B → B 不显示生成中；完成不改变 B；切回 A 显示已生成。</summary>
        [Fact]
        public async Task GenerateA_SwitchB_CompletionDoesNotAlterB_SwitchBackShowsGenerated()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-b");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voice: voice);
            OpenDetail(viewModel, "s-a");                           // 恢复 A 分析 → 自动预生成触发

            await WaitUntilAsync(() => viewModel.IsCurrentDetailVoiceBusy);

            OpenDetail(viewModel, "s-b");
            Assert.False(viewModel.IsCurrentDetailVoiceBusy);       // B 不显示 A 的生成中
            Assert.Equal("未生成", viewModel.VoiceStateText);

            voice.Complete();
            await WaitUntilAsync(() => analysisStore.HasCachedVoice("s-a"));

            Assert.Equal("未生成", viewModel.VoiceStateText);       // B 依旧不变
            OpenDetail(viewModel, "s-a");
            Assert.Equal("语音已生成 · 播放将直接使用缓存", viewModel.VoiceStateText);
            Assert.Equal(1, voice.Calls);
        }

        /// <summary>Gate K(13)：切回 A → 播放可用且命中缓存（零额外合成）。</summary>
        [Fact]
        public async Task SwitchBackA_Playable_FromCache()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            analysisStore.SaveVoiceWav("s-a", TestWav.Create());
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voice: voice);
            OpenDetail(viewModel, "s-a");

            Assert.True(viewModel.PlaySpokenSummaryCommand.CanExecute(null));
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);

            Assert.Equal(0, voice.Calls);                           // 缓存命中 → 零合成
            Assert.Equal(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
        }

        /// <summary>Gate K(14)+(15) VM 级：损坏缓存不被当有效（自动重新合成）；SpokenSummary 为空 → 无语音路径。</summary>
        [Fact]
        public async Task CorruptCache_TriggersResynthesis_EmptySpokenSummary_NoVoice()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, "s-a");
            SaveSession(store, "s-e");
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-a", T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-e", T0, "test-model", 100, false,
                MakeAnalysisResult() with { SpokenSummary = "" }, "{}"));
            var directory = Path.Combine(_temp.FullPath, "s-a");
            File.WriteAllBytes(Path.Combine(directory, "voice.wav"), new byte[] { 1, 2, 3 });
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voice: voice);

            OpenDetail(viewModel, "s-a");
            await WaitUntilAsync(() => voice.Calls == 1);           // 损坏 → 未命中 → 重新合成
            voice.Complete();
            await WaitUntilAsync(() => analysisStore.HasCachedVoice("s-a"));

            OpenDetail(viewModel, "s-e");
            Assert.True(string.IsNullOrEmpty(viewModel.SpokenSummary));   // 空 SpokenSummary
            Assert.Equal("未生成", viewModel.VoiceStateText);             // 无语音路径
        }

        /// <summary>Gate K(16) 补充：缓存查询不依赖任何内存 index——直接新建 store 读同一磁盘目录即命中。</summary>
        [Fact]
        public void CacheLookup_RequiresNoPriorSynthesizeCall_InFreshStore()
        {
            var first = new SessionAnalysisStore(_temp.FullPath);
            first.SaveVoiceWav("s-x", TestWav.Create());

            var freshStore = new SessionAnalysisStore(_temp.FullPath);   // 进程级“新实例”
            Assert.True(freshStore.HasCachedVoice("s-x"));
            Assert.NotNull(freshStore.TryLoadVoiceWav("s-x"));
        }

        // ==================== M4.5E.3 Gate I：Voice Playback 回归 ====================

        /// <summary>M4.5E.3：准备“已分析（+可选缓存）”的会话，供播放路径测试使用。</summary>
        private SessionAnalysisStore MakeAnalyzedSession(TelemetrySessionStore store, string id, byte[]? wav = null)
        {
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(store, id);
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, id, T0, "test-model", 100, false, MakeAnalysisResult(), "{}"));
            if (wav is not null)
            {
                analysisStore.SaveVoiceWav(id, wav);
            }

            return analysisStore;
        }

        /// <summary>Gate I(1)+(2)：冷启动缓存 WAV → 播放后端收到与磁盘逐字节一致的 bytes，零合成调用。</summary>
        [Fact]
        public async Task Play_CachedWav_ColdStart_BackendReceivesExactDiskBytes_ZeroSynthesis()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var wav = TestWav.Create();
            MakeAnalyzedSession(store, "s-a", wav);
            var playback = new RecordingWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voiceEndpoint: "", voice: voice, playback: playback);

            OpenDetail(viewModel, "s-a");
            Assert.True(viewModel.PlaySpokenSummaryCommand.CanExecute(null));
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);

            Assert.Equal(0, voice.Calls);                       // 绕过 GSV：缓存命中零合成
            Assert.Equal(1, playback.Calls);
            Assert.Equal(wav, playback.Received[0]);            // 播放字节 == 磁盘字节
            Assert.Equal(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
        }

        /// <summary>
        /// M4.5E.3 Gate E 回归锁：语音后台回调的 UI 状态变更必须经构造时捕获的
        /// SynchronizationContext 路由。E.2 直接在后台线程 set_VoiceState → 同步触发
        /// CanExecuteChanged 的 VerifyAccess InvalidOperationException，且发生在
        /// PlayWav 之前 → 播放无声、无报错（真机日志实锤）。锁定：Playing 转换
        /// 已通过 Post 路由，播放才被调用；完成回写同样经路由归位。
        /// </summary>
        [Fact]
        public async Task Play_VoiceUiState_RoutesThroughCapturedSynchronizationContext()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var wav = TestWav.Create();
            MakeAnalyzedSession(store, "s-a", wav);
            var playback = new RecordingWavPlayback();
            var voice = new GatedVoiceService();
            var recorded = new RecordingSynchronizationContext();

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(recorded);
            SessionsViewModel viewModel;
            try
            {
                (viewModel, _) = Create(voiceEndpoint: "", voice: voice, playback: playback);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            playback.OnFirstPlaySnapshot = () => recorded.PostCount;
            OpenDetail(viewModel, "s-a");
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => playback.Calls == 1);
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);

            Assert.Equal(1, playback.PostCountAtFirstPlay);     // Playing 转换在 PlayWav 之前已路由
            Assert.True(recorded.PostCount >= 2);               // 完成回写也经上下文路由
            Assert.Equal(0, voice.Calls);
            Assert.Equal(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
        }

        /// <summary>Gate I(6)+(7)+(8)：A 播放中切 B → B 不显示播放/忙；完成回写不改变 B 的语音 UI。</summary>
        [Fact]
        public async Task PlayA_SwitchB_BNeverShowsPlaying_CompletionLeavesBUntouched()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            var wavA = TestWav.Create();
            wavA[12] = 0xA1;
            MakeAnalyzedSession(store, "s-a", wavA);
            MakeAnalyzedSession(store, "s-b");                  // B 已分析、无缓存
            var playback = new GatedWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voiceEndpoint: "", voice: voice, playback: playback);

            OpenDetail(viewModel, "s-a");
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => playback.Calls == 1);
            Assert.Equal(wavA, playback.Received[0]);           // 播放的是 A 的缓存
            Assert.True(viewModel.IsCurrentDetailVoiceBusy);    // 忙状态只在 A 可见

            OpenDetail(viewModel, "s-b");
            Assert.False(viewModel.IsCurrentDetailVoiceBusy);   // B 不显示 A 的播放中
            Assert.Equal("未生成", viewModel.VoiceStateText);    // B 无缓存 → 不显示“播放中”

            playback.Release();
            await WaitUntilAsync(() =>
                viewModel.VoiceState == SessionsViewModel.VoicePlaybackState.Idle);
            Assert.Equal("未生成", viewModel.VoiceStateText);    // 完成回写不改变 B
            Assert.False(viewModel.IsCurrentDetailVoiceBusy);
        }

        /// <summary>Gate I(9)+Gate F：播放操作失败 → 可见简短失败态，不再“无声无息”。</summary>
        [Fact]
        public async Task Play_SynthesisFails_ShowsVisiblePlaybackFailureState()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            MakeAnalyzedSession(store, "s-a");                  // 已分析、无缓存 → 播放时走合成
            var playback = new RecordingWavPlayback();
            var (viewModel, _) = Create(voiceEndpoint: "", voice: new FailingVoiceService(), playback: playback);

            OpenDetail(viewModel, "s-a");
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() =>
                viewModel.VoiceState == SessionsViewModel.VoicePlaybackState.Error);

            Assert.Equal("语音播放失败", viewModel.VoiceStateText);
            Assert.True(viewModel.HasError);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorText));   // 技术细节可诊断
            Assert.Equal(0, playback.Calls);                    // 失败绝不进入播放后端
        }

        /// <summary>Gate I(4)：损坏 WAV → 不是有效缓存 → 播放后端绝不收到损坏字节，走重新合成。</summary>
        [Fact]
        public async Task Play_CorruptCache_NeverReachesPlayback_Resynthesizes()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            MakeAnalyzedSession(store, "s-a");
            var corrupt = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(Path.Combine(_temp.FullPath, "s-a", "voice.wav"), corrupt);
            var playback = new RecordingWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voice: voice, playback: playback);

            OpenDetail(viewModel, "s-a");                       // 损坏 → 缓存未命中 → 自动预生成
            await WaitUntilAsync(() => voice.Calls == 1);
            voice.Complete();
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);

            viewModel.PlaySpokenSummaryCommand.Execute(null);   // 立即播放新合成结果
            await WaitUntilAsync(() => playback.Calls == 1);

            Assert.Equal(TestWav.Create(), playback.Received[0]);   // 播放的是新合成字节
            Assert.All(playback.Received, bytes => Assert.NotEqual(corrupt, bytes));
        }

        /// <summary>Gate I(5)：空 WAV → 不是有效缓存 → 播放路径按未命中重新合成，绝不播放空字节。</summary>
        [Fact]
        public async Task Play_EmptyCacheFile_TreatedAsMiss_PlaysResynthesizedBytes()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            MakeAnalyzedSession(store, "s-a");
            File.WriteAllBytes(Path.Combine(_temp.FullPath, "s-a", "voice.wav"), Array.Empty<byte>());
            var playback = new RecordingWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voiceEndpoint: "", voice: voice, playback: playback);

            OpenDetail(viewModel, "s-a");
            viewModel.PlaySpokenSummaryCommand.Execute(null);   // 空 WAV → 未命中 → 合成
            voice.Complete();
            await WaitUntilAsync(() => playback.Calls == 1);

            Assert.Equal(1, voice.Calls);
            Assert.Equal(TestWav.Create(), playback.Received[0]);
            Assert.All(playback.Received, bytes => Assert.NotEmpty(bytes));
        }

        /// <summary>Gate H：合成 → 落盘 → 用户点击播放 → 缓存命中；生成/落盘/播放三处字节一致。</summary>
        [Fact]
        public async Task GenerateB_Save_ThenPlay_BytesConsistentAcrossCacheReload()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            MakeAnalyzedSession(store, "s-b");
            var playback = new RecordingWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voice: voice, playback: playback);

            OpenDetail(viewModel, "s-b");                       // 恢复分析 → 自动预生成
            await WaitUntilAsync(() => voice.Calls == 1);
            voice.Complete();
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);

            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            Assert.True(analysisStore.HasCachedVoice("s-b"));   // 磁盘事实源已落盘
            var cached = analysisStore.TryLoadVoiceWav("s-b");

            viewModel.PlaySpokenSummaryCommand.Execute(null);   // 立即播放（不重调 GSV）
            await WaitUntilAsync(() => playback.Calls == 1);

            Assert.Equal(1, voice.Calls);                       // 播放零额外合成
            Assert.NotNull(cached);
            Assert.Equal(cached, playback.Received[0]);         // 生成 == 缓存重读 == 播放
            Assert.Equal(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
        }

        /// <summary>Gate I(12)：同一会话重复播放两轮都成功，均零合成。</summary>
        [Fact]
        public async Task RepeatedPlay_SameSession_BothRoundsHitCache()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            MakeAnalyzedSession(store, "s-a", TestWav.Create());
            var playback = new RecordingWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voiceEndpoint: "", voice: voice, playback: playback);

            OpenDetail(viewModel, "s-a");
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);
            Assert.Equal(1, playback.Calls);

            Assert.True(viewModel.PlaySpokenSummaryCommand.CanExecute(null));
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => playback.Calls == 2);
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);

            Assert.Equal(0, voice.Calls);
            Assert.Equal(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
        }

        /// <summary>
        /// Gate I(13)+(14)：播放所有权唯一——播放中禁止第二播、状态不提前归位
        ///（ownership lifetime 锁：播放后端持有播放直到 PlayWav 返回，VM 不得提前清态）；
        /// 播放完成后所有权释放、可再次播放。
        /// </summary>
        [Fact]
        public async Task Play_SecondClick_BlockedWhilePlaying_StateHeldUntilCompletion()
        {
            var store = new TelemetrySessionStore(_temp.FullPath);
            MakeAnalyzedSession(store, "s-a", TestWav.Create());
            var playback = new GatedWavPlayback();
            var voice = new GatedVoiceService();
            var (viewModel, _) = Create(voiceEndpoint: "", playback: playback);

            OpenDetail(viewModel, "s-a");
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            await WaitUntilAsync(() => playback.Calls == 1);

            Assert.NotEqual(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
            Assert.False(viewModel.PlaySpokenSummaryCommand.CanExecute(null));   // 播放中禁用

            playback.Release();
            await WaitUntilAsync(() => !viewModel.IsCurrentDetailVoiceBusy);
            Assert.Equal(SessionsViewModel.VoicePlaybackState.Idle, viewModel.VoiceState);
            Assert.True(viewModel.PlaySpokenSummaryCommand.CanExecute(null));    // 释放后可再播
        }
    }
}