using System;
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
using AIGeekTuner.Tests.Services.Incidents;
using AIGeekTuner.Tests.TestSupport;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels
{
    /// <summary>
    /// Gate O：Windows 事件证据展示（VM/presentation）。
    /// 全部用真实 store + TempDirectory，不读真实事件日志、不调真实 AI。
    /// </summary>
    public sealed class SessionsIncidentPresentationTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private static readonly DateTimeOffset T0 =
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

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

        private sealed class FakeIncidentCorrelation : ISessionIncidentCorrelationService
        {
            public Task<SessionIncidentEnvelope> CaptureAsync(
                TelemetryRecordingSession session, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("presentation tests never record");
        }

        private SessionsViewModel CreateViewModel()
        {
            return new SessionsViewModel(
                new TelemetryRecordingService(new EmptyHub()),
                new TelemetrySessionStore(_temp.FullPath),
                new FakeSettingsService(),
                new FakeAnalysisService(),
                new SessionAnalysisStore(_temp.FullPath),
                new FakeVoiceService(),
                new FakeWavPlayback(),
                () => new VoiceConfiguration("http://localhost:9880", "", "", "", "zh", 1.0, null, null),
                new FakeIncidentCorrelation(),
                new SessionIncidentStore(_temp.FullPath));
        }

        private static TelemetryRecordingSession SaveSession(TelemetrySessionStore store, string id)
        {
            var session = TelemetryRecordingSession.Start(1000, T0) with
            {
                Id = id,
                Status = RecordingStatus.Completed,
                CompletedAtUtc = T0.AddSeconds(30),
            };
            store.Save(session);
            return session;
        }

        private SessionIncidentEnvelope SaveEnvelope(
            string sessionId, IReadOnlyList<WindowsIncident> incidents,
            IncidentQueryStatus status = IncidentQueryStatus.Success)
        {
            var envelope = SessionIncidentStoreTests.MakeEnvelope(sessionId, status, incidents);
            new SessionIncidentStore(_temp.FullPath).Save(envelope);
            return envelope;
        }

        private static WindowsIncident Incident(int index) =>
            SessionIncidentStoreTests.MakeIncident(index);

        private void SelectSession(SessionsViewModel viewModel, string sessionId)
        {
            viewModel.SelectedRecent =
                viewModel.RecentSessions.First(item => item.Id == sessionId);
            viewModel.SelectRecentCommand.Execute(null);
        }

        private sealed class RecordingHub : ITelemetryHub
        {
            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(TelemetrySnapshot.Empty(DateTimeOffset.UtcNow));
        }

        private sealed class WorkingIncidentCorrelation : ISessionIncidentCorrelationService
        {
            public Task<SessionIncidentEnvelope> CaptureAsync(
                TelemetryRecordingSession session, CancellationToken cancellationToken = default) =>
                Task.FromResult(SessionIncidentStoreTests.MakeEnvelope(session.Id));
        }

        /// <summary>
        /// V2-M4.5D Gate Q 回归 + M4.5E.1 语义更新：录制结束必须自动进入刚完成会话的
        /// Detail（CurrentDetailSessionId = 新会话），分析按钮以其为准真正可执行。
        /// </summary>
        [Fact]
        public async Task StopAndAnalyze_ShowsDetailOfNewSession_AnalyzeCommandActuallyRuns()
        {
            var settings = new FakeSettingsService();
            settings.Current = new ApplicationSettings { RecordingIntervalMs = 200 };
            var store = new TelemetrySessionStore(_temp.FullPath);
            var viewModel = new SessionsViewModel(
                // 与生产 composition root 一致：recorder 与 VM 共用同一 store，
                // 否则 StopAsync 不落盘，LoadRecent 读不到任何会话。
                new TelemetryRecordingService(new RecordingHub(), store),
                store,
                settings,
                new FakeAnalysisService(),
                new SessionAnalysisStore(_temp.FullPath),
                new FakeVoiceService(),
                new FakeWavPlayback(),
                () => new VoiceConfiguration("http://localhost:9880", "", "", "", "zh", 1.0, null, null),
                new WorkingIncidentCorrelation(),
                new SessionIncidentStore(_temp.FullPath));

            viewModel.StartRecordingCommand.Execute(null);
            Assert.True(viewModel.IsRecording);
            await Task.Delay(700);   // 200ms 间隔 → 若干真实采样
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();

            // 回归锁 1：Detail 展示的就是刚完成的新会话（M4.5E.1：不再依赖列表选择）。
            Assert.NotNull(viewModel.CurrentDetailSessionId);
            Assert.True(viewModel.RecentSessions[0].SampleCount > 0);
            // 回归锁 2：分析按钮真正可执行（不再以可用外观静默 no-op）。
            Assert.True(viewModel.AnalyzeCommand.CanExecute(null));

            // 回归锁 3：AnalyzeAsync 真正跑过分析路径——FakeAnalysisService 返回失败，
            // 若执行过则 AnalysisError 被赋值；静默 return 时它保持空。
            await viewModel.AnalyzeCommand.ExecuteAsync();
            Assert.Contains("AI 分析失败", viewModel.AnalysisError);
        }

        private sealed class SuccessAnalysisService : ISessionAnalysisService
        {
            public Task<SessionAnalysisRun> AnalyzeAsync(DiagnosticEvidenceContext context, CancellationToken cancellationToken) =>
                Task.FromResult(new SessionAnalysisRun(
                    true,
                    new SessionAnalysisResult(
                        "总体正常",
                        SessionOverallAssessment.Normal,
                        0.9,
                        Array.Empty<SessionFinding>(),
                        Array.Empty<SessionRecommendation>(),
                        Array.Empty<string>(),
                        "语音摘要测试文本"),
                    null,
                    Array.Empty<string>(),
                    1,
                    TimeSpan.FromSeconds(1),
                    "test-model",
                    false,
                    "{}"));
        }

        private sealed class CountingVoiceService : IVoiceSynthesisService
        {
            public int Calls { get; private set; }

            public Task<VoiceSynthesisResult> SynthesizeAsync(
                string text, VoiceConfiguration configuration, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(VoiceSynthesisResult.Ok(TestWav.Create()));
            }
        }

        /// <summary>
        /// V2-M4.5D：分析成功 → 自动预生成语音并写入 Sessions/{id}/voice.wav；
        /// 再次播放命中缓存，不再调用 GPT-SoVITS（用户需求：自动生成并保存）。
        /// </summary>
        [Fact]
        public async Task AnalyzeSuccess_AutoGeneratesVoiceWav_PlayReusesCache()
        {
            var settings = new FakeSettingsService();
            settings.Current = new ApplicationSettings { RecordingIntervalMs = 200 };
            var store = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            var voice = new CountingVoiceService();
            var viewModel = new SessionsViewModel(
                new TelemetryRecordingService(new RecordingHub(), store),
                store,
                settings,
                new SuccessAnalysisService(),
                analysisStore,
                voice,
                new FakeWavPlayback(),
                () => new VoiceConfiguration("http://127.0.0.1:9880", "ref.wav", "p", "zh", "zh", 1.0, null, null),
                new WorkingIncidentCorrelation(),
                new SessionIncidentStore(_temp.FullPath));

            viewModel.StartRecordingCommand.Execute(null);
            await Task.Delay(700);
            await viewModel.StopAndAnalyzeCommand.ExecuteAsync();
            await viewModel.AnalyzeCommand.ExecuteAsync();

            // 自动预生成：分析完成后 voice.wav 自动落盘。
            var sessionId = viewModel.CurrentDetailSessionId!;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!analysisStore.VoiceWavExists(sessionId) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
            Assert.True(analysisStore.VoiceWavExists(sessionId));
            Assert.Equal(1, voice.Calls);   // 预生成恰好调用一次合成

            // 播放：命中会话级缓存 → 不再新增合成调用。
            viewModel.PlaySpokenSummaryCommand.Execute(null);
            var deadline2 = DateTime.UtcNow.AddSeconds(10);
            while (viewModel.VoiceState == SessionsViewModel.VoicePlaybackState.Playing
                && DateTime.UtcNow < deadline2)
            {
                await Task.Delay(100);
            }
            Assert.Equal(1, voice.Calls);
        }

        [Fact]
        public void OldSession_NoIncidentsFile_ShowsNotCaptured()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-old");
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-old");

            Assert.True(viewModel.HasResult);                       // 会话本体正常
            Assert.False(viewModel.HasIncidentRows);
            Assert.True(viewModel.HasIncidentStateText);
            Assert.Contains("未采集 Windows 事件证据", viewModel.IncidentStateText);
        }

        [Fact]
        public void EmptyIncidents_ShowsNoIncidentsFound_DistinctFromNotCaptured()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-empty");
            SaveEnvelope("s-empty", []);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-empty");

            Assert.True(viewModel.HasIncidentStateText);
            Assert.Contains("本次关联窗口内未发现已识别的 Windows 事件", viewModel.IncidentStateText);
            Assert.DoesNotContain("未采集", viewModel.IncidentStateText);
            Assert.False(viewModel.HasIncidentRows);
        }

        [Fact]
        public void Success_Incidents_Listed_WithCountHeader()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-ok");
            SaveEnvelope("s-ok", [Incident(0), Incident(1)]);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-ok");

            Assert.True(viewModel.HasIncidentRows);
            Assert.False(viewModel.HasIncidentStateText);
            Assert.False(viewModel.HasIncidentQualityText);
            Assert.Equal("Windows 事件证据 · 2 条", viewModel.IncidentHeaderText);
            Assert.Equal(2, viewModel.IncidentRows.Count);
        }

        [Fact]
        public void PartialStatus_ShowsDataQualityHint()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-partial");
            SaveEnvelope("s-partial", [Incident(0)], IncidentQueryStatus.Partial);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-partial");

            Assert.True(viewModel.HasIncidentRows);                 // 已取得的事件仍显示
            Assert.True(viewModel.HasIncidentQualityText);
            Assert.Equal("部分事件日志不可用", viewModel.IncidentQualityText);
        }

        [Fact]
        public void PermissionDenied_ShowsPermissionHint()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-denied");
            SaveEnvelope("s-denied", [], IncidentQueryStatus.PermissionDenied);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-denied");

            // 0 条 + 非 Success：走状态文案 + quality 提示双路径。
            Assert.True(viewModel.HasIncidentStateText);
            Assert.True(viewModel.HasIncidentQualityText);
            Assert.Equal("没有读取事件日志的权限", viewModel.IncidentQualityText);
        }

        [Fact]
        public void Category_UsesUserFacingDisplayName()
        {
            Assert.Equal("非正常关机", SessionIncidentRow.CategoryDisplayName(IncidentCategory.UnexpectedShutdown));
            Assert.Equal("蓝屏 / BugCheck", SessionIncidentRow.CategoryDisplayName(IncidentCategory.BugCheck));
            Assert.Equal("硬件错误", SessionIncidentRow.CategoryDisplayName(IncidentCategory.HardwareError));
            Assert.Equal("显示驱动事件", SessionIncidentRow.CategoryDisplayName(IncidentCategory.DisplayDriver));
            Assert.Equal("存储事件", SessionIncidentRow.CategoryDisplayName(IncidentCategory.Storage));
            Assert.Equal("应用崩溃", SessionIncidentRow.CategoryDisplayName(IncidentCategory.ApplicationCrash));
            Assert.Equal("应用无响应", SessionIncidentRow.CategoryDisplayName(IncidentCategory.ApplicationHang));
            Assert.Equal("Windows 错误报告", SessionIncidentRow.CategoryDisplayName(IncidentCategory.WindowsErrorReporting));
        }

        [Fact]
        public void Severity_Maps_ToWindowsEventLevelLabel_AndThemeDot()
        {
            Assert.Equal("Critical", SessionIncidentRow.SeverityLevelLabel(IncidentSeverity.Critical));
            Assert.Equal("Error", SessionIncidentRow.SeverityLevelLabel(IncidentSeverity.Error));
            Assert.Equal("Warning", SessionIncidentRow.SeverityLevelLabel(IncidentSeverity.Warning));
            Assert.Equal("Information", SessionIncidentRow.SeverityLevelLabel(IncidentSeverity.Information));

            var row = new SessionIncidentRow(Incident(0), isInAiContext: false);
            Assert.Contains("Windows 事件级别", row.SeverityToolTip);
            Assert.DoesNotContain("危险", row.SeverityToolTip);
            Assert.DoesNotContain("故障", row.SeverityToolTip);
            Assert.NotNull(row.SeverityBrush);
            Assert.DoesNotContain("incident:", row.CategoryDisplay); // 不显示原始 ID / enum 名
        }

        [Fact]
        public void DisplayList_IsCapped_At50_WithOverflowNotice()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-cap");
            var incidents = Enumerable.Range(0, 60).Select(Incident).ToArray();
            SaveEnvelope("s-cap", incidents);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-cap");

            Assert.Equal(SessionsViewModel.IncidentDisplayCap, viewModel.IncidentRows.Count);
            Assert.True(viewModel.HasIncidentOverflow);
            Assert.Contains("已显示前 50 条", viewModel.IncidentOverflowText);
            Assert.Contains("共 60 条", viewModel.IncidentOverflowText);
        }

        [Fact]
        public void IncidentRows_Keep_ChronologicalOrder()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-order");
            // MakeIncident(index) → OccurredAtUtc = T0 + index 分钟（升序保存）。
            // 顺序断言走 Tooltip 里的 EvidenceId，避免依赖测试机时区。
            SaveEnvelope("s-order", [Incident(0), Incident(1), Incident(2)]);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-order");

            Assert.Equal(
                ["incident:0001", "incident:0002", "incident:0003"],
                viewModel.IncidentRows.Select(row => row.RowToolTip
                    .Substring(row.RowToolTip.IndexOf("EvidenceId: ", StringComparison.Ordinal)
                        + "EvidenceId: ".Length)).ToArray());
        }

        [Fact]
        public void AiIncludedIncident_IsMarked()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(sessionStore, "s-ai");
            SaveEnvelope("s-ai", [Incident(0), Incident(1)]);
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-ai", T0, "fake", 100, false,
                MakeAnalysisResult(), MakeCombinedContextJson("s-ai", ["incident:0001"])));
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-ai");

            Assert.True(viewModel.IncidentRows.Single(row => row.EvidenceId == "incident:0001").HasAiMarker);
        }

        [Fact]
        public void OmittedIncident_IsNotMarkedAsAiEvidence()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            SaveSession(sessionStore, "s-omit");
            SaveEnvelope("s-omit", [Incident(0), Incident(1)]);
            // reducer 上下文只含 incident:0001；incident:0002 被 omitted。
            analysisStore.Save(new SessionAnalysisEnvelope(
                1, "s-omit", T0, "fake", 100, false,
                MakeAnalysisResult(), MakeCombinedContextJson("s-omit", ["incident:0001"])));
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-omit");

            Assert.True(viewModel.IncidentRows.Single(row => row.EvidenceId == "incident:0001").HasAiMarker);
            Assert.False(viewModel.IncidentRows.Single(row => row.EvidenceId == "incident:0002").HasAiMarker);
        }

        [Fact]
        public void IncidentEvidenceId_Maps_ToReadableFindingText()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-map");
            SaveEnvelope("s-map", [Incident(0)]);
            var viewModel = CreateViewModel();
            SelectSession(viewModel, "s-map");

            var text = viewModel.DescribeEvidence("incident:0001");

            Assert.DoesNotContain("incident:", text);        // 原始 ID 不面向用户
            Assert.Contains("非正常关机", text);               // 类别可读（MakeIncident(0) = UnexpectedShutdown）
            Assert.Contains("Event 41", text);                // Provider/EventId 保留英文技术事实
        }

        [Fact]
        public void UnknownIncidentEvidence_FallsBack_Gracefully()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-fallback");
            SaveEnvelope("s-fallback", [Incident(0)]);
            var viewModel = CreateViewModel();
            SelectSession(viewModel, "s-fallback");

            var text = viewModel.DescribeEvidence("incident:9999");

            Assert.Equal("Windows 事件证据不可用", text);
        }

        [Fact]
        public void AnalysisAbsent_NoAiMarkers()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-noai");
            SaveEnvelope("s-noai", [Incident(0), Incident(1)]);
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-noai");

            Assert.All(viewModel.IncidentRows, row => Assert.False(row.HasAiMarker));
        }

        [Fact]
        public void CorruptIncidentsJson_DoesNotBreakSessionLoad()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            SaveSession(sessionStore, "s-corrupt");
            var directory = Path.Combine(_temp.FullPath, "s-corrupt");
            File.WriteAllText(Path.Combine(directory, "incidents.json"), "{ not valid json !!!");
            var viewModel = CreateViewModel();

            SelectSession(viewModel, "s-corrupt");

            // 会话本体照常；事件证据按未采集处理（Load null → NotCaptured 文案）。
            Assert.True(viewModel.HasResult);
            Assert.False(viewModel.HasIncidentRows);
            Assert.Contains("未采集 Windows 事件证据", viewModel.IncidentStateText);
        }

        private static SessionAnalysisResult MakeAnalysisResult() => new(
            Summary: "结合遥测与事件证据的说明。",
            OverallAssessment: SessionOverallAssessment.Attention,
            Confidence: 0.7,
            Findings: [new SessionFinding("t", SessionFindingCategory.Stability, "a", "e", ["incident:0001"])],
            Recommendations: [],
            Uncertainties: [],
            SpokenSummary: "口语化摘要，长度必须足够满足校验规则。");

        private static string MakeCombinedContextJson(string sessionId, IReadOnlyList<string> evidenceIds) =>
            "{\"Metadata\":{\"SessionId\":\"" + sessionId + "\"},"
            + "\"WindowsIncidents\":{\"Incidents\":["
            + string.Join(",", evidenceIds.Select(id => "{\"EvidenceId\":\"" + id + "\"}"))
            + "]}}";
    }
}
