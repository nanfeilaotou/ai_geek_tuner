using System.IO;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording
{
    /// <summary>
    /// M4.1 Gate 0.4：生产持久化接线回归。
    /// Problem A：Recorder 必须注入 store 才会落盘 session.json；
    /// Problem B：分析成功必须通过 store 落盘 analysis.json 且可重载。
    /// </summary>
    public sealed class SessionPersistenceGateTests : IDisposable
    {
        private readonly string _root;

        public SessionPersistenceGateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "aigeektuner-gate04", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private sealed class EmptyHub : ITelemetryHub
        {
            public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(TelemetrySnapshot.Empty(DateTimeOffset.UtcNow));
        }

        [Fact]
        public async Task RecorderWithStore_SavesSessionJson_AndRoundTrips()
        {
            var store = new TelemetrySessionStore(_root);
            var recorder = new TelemetryRecordingService(new EmptyHub(), store);

            Assert.True(recorder.Start(200));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (recorder.CurrentSession is null || recorder.CurrentSession.Samples.Count == 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("recorder did not capture a sample within 5s");
                }

                await Task.Delay(50);
            }

            var session = await recorder.StopAsync();

            Assert.NotNull(session);
            Assert.NotNull(recorder.LastSavedPath);
            Assert.True(File.Exists(recorder.LastSavedPath), "session.json must exist after StopAsync");

            var reloaded = store.Load(session!.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(session.Id, reloaded!.Id);
            Assert.True(reloaded.Samples.Count > 0);
            Assert.Equal(session.Samples.Count, reloaded.Samples.Count);
        }

        [Fact]
        public async Task RecorderWithoutStore_DoesNotSave()
        {
            // 回归锚点：未注入 store 时 LastSavedPath 必须保持 null（问题 A 的原始缺陷语义）。
            var recorder = new TelemetryRecordingService(new EmptyHub());
            Assert.True(recorder.Start(200));
            await Task.Delay(300);
            var session = await recorder.StopAsync();

            Assert.NotNull(session);
            Assert.Null(recorder.LastSavedPath);
        }

        private static SessionAnalysisEnvelope MakeEnvelope(
            string sessionId, string summary, long durationMs) =>
            new(
                SchemaVersion: 1,
                SessionId: sessionId,
                AnalyzedAtUtc: DateTimeOffset.UtcNow,
                ModelName: "qwen3:8b",
                DurationMs: durationMs,
                RepairUsed: false,
                Result: new SessionAnalysisResult(
                    Summary: summary,
                    OverallAssessment: SessionOverallAssessment.Normal,
                    Confidence: 0.9,
                    Findings:
                    [
                        new SessionFinding(
                            "温度平稳", SessionFindingCategory.Thermal, "正常", "全程低于阈值",
                            ["stat:cpu:cpu.package.temperature"]),
                    ],
                    Recommendations: [new SessionRecommendation("保持当前散热条件")],
                    Uncertainties: ["样本时长较短"],
                    SpokenSummary: "本次录制未发现异常。"),
                ContextJson: "{\"sessionId\":\"" + sessionId + "\"}");

        [Fact]
        public void AnalysisSave_CreatesAnalysisJson_AndRoundTrips()
        {
            var analysisStore = new SessionAnalysisStore(_root);
            var sessionStore = new TelemetrySessionStore(_root);
            var session = TelemetryRecordingSession.Start(1000, DateTimeOffset.UtcNow);
            sessionStore.Save(session with { Status = RecordingStatus.Completed });

            var envelope = MakeEnvelope(session.Id, "一切正常", 29000);
            analysisStore.Save(envelope);

            var path = Path.Combine(_root, session.Id, "analysis.json");
            Assert.True(File.Exists(path));

            var loaded = analysisStore.Load(session.Id);
            Assert.NotNull(loaded);
            Assert.Equal(session.Id, loaded!.SessionId);
            Assert.Equal("qwen3:8b", loaded.ModelName);
            Assert.Equal(29000, loaded.DurationMs);
            Assert.False(loaded.RepairUsed);
            Assert.Equal("一切正常", loaded.Result.Summary);
            Assert.Equal(SessionOverallAssessment.Normal, loaded.Result.OverallAssessment);
            Assert.Single(loaded.Result.Findings);
            Assert.Equal("本次录制未发现异常。", loaded.Result.SpokenSummary);
        }

        [Fact]
        public void ReAnalysis_OverwritesExistingAnalysis()
        {
            var analysisStore = new SessionAnalysisStore(_root);
            var first = MakeEnvelope("s-overwrite", "第一版", 1000);
            var second = MakeEnvelope("s-overwrite", "重分析后的结论", 4500) with { RepairUsed = true };

            analysisStore.Save(first);
            analysisStore.Save(second);

            var loaded = analysisStore.Load("s-overwrite");
            Assert.NotNull(loaded);
            Assert.Equal("重分析后的结论", loaded!.Result.Summary);
            Assert.Equal(4500, loaded.DurationMs);
            Assert.True(loaded.RepairUsed);
        }

        [Fact]
        public void CorruptAnalysisJson_ReturnsNull_AndLeavesSessionJsonIntact()
        {
            var analysisStore = new SessionAnalysisStore(_root);
            var sessionStore = new TelemetrySessionStore(_root);
            var session = TelemetryRecordingSession.Start(1000, DateTimeOffset.UtcNow);
            sessionStore.Save(session with { Status = RecordingStatus.Completed });

            var directory = Path.Combine(_root, session.Id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "analysis.json"), "{ not valid json !!!");

            Assert.Null(analysisStore.Load(session.Id)); // 不抛异常，返回 null

            var reloadedSession = sessionStore.Load(session.Id);
            Assert.NotNull(reloadedSession);             // session.json 完全不受影响
            Assert.Equal(session.Id, reloadedSession!.Id);
        }

        [Fact]
        public void Load_MissingAnalysis_ReturnsNull()
        {
            var analysisStore = new SessionAnalysisStore(_root);
            Assert.Null(analysisStore.Load("no-such-session"));
        }
    }
}
