using System.IO;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.Incidents
{
    /// <summary>
    /// Gate G：Session ↔ Windows Incident correlation。
    /// 全部使用 fake IIncidentSource，绝不读真实 Windows Event Log。
    /// </summary>
    public sealed class SessionIncidentCorrelationTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public SessionIncidentCorrelationTests() { }

        public void Dispose() => _temp.Dispose();

        private static DateTimeOffset T(int minutes) =>
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static TelemetryRecordingSession MakeSession(
            DateTimeOffset start, DateTimeOffset end, bool completed = true) =>
            TelemetryRecordingSession.Start(1000, start) with
            {
                Status = completed ? RecordingStatus.Completed : RecordingStatus.Recording,
                CompletedAtUtc = completed ? end : null,
            };

        private SessionIncidentCorrelationService CreateService(FakeIncidentSource source) =>
            new(source, new SessionIncidentStore(_temp.FullPath));

        [Fact]
        public async Task CompletedSession_Queries_BufferedWindow_WithMaxResultsCap()
        {
            var source = new FakeIncidentSource();
            var service = CreateService(source);
            var start = T(0);
            var end = T(30);
            var session = MakeSession(start, end);

            var envelope = await service.CaptureAsync(session);

            // start 前 30 秒 → completed 后 30 秒（固定 buffer，非因果窗口）。
            Assert.NotNull(source.LastQuery);
            Assert.Equal(start - TimeSpan.FromSeconds(30), source.LastQuery!.StartUtc);
            Assert.Equal(end + TimeSpan.FromSeconds(30), source.LastQuery.EndUtc);
            Assert.Equal(IncidentQuery.MaxResultsCap, source.LastQuery.MaxResults);

            Assert.Equal(session.Id, envelope.SessionId);
            Assert.Equal(start - TimeSpan.FromSeconds(30), envelope.WindowStartUtc);
            Assert.Equal(end + TimeSpan.FromSeconds(30), envelope.WindowEndUtc);
            Assert.Equal(TimeSpan.FromSeconds(30), envelope.PreBuffer);
            Assert.Equal(TimeSpan.FromSeconds(30), envelope.PostBuffer);
            Assert.True(File.Exists(Path.Combine(_temp.FullPath, session.Id, "incidents.json")));
        }

        [Fact]
        public async Task ActiveSession_WithoutCompletedAtUtc_IsRejected()
        {
            var source = new FakeIncidentSource();
            var service = CreateService(source);
            var session = MakeSession(T(0), T(30), completed: false);

            // 明确拒绝：绝不偷偷用 UtcNow 补齐结束时间。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CaptureAsync(session));
            Assert.Null(source.LastQuery); // 根本没有发起事件查询
        }

        [Fact]
        public async Task QueryStatus_Partial_IsPreserved_Into_Envelope()
        {
            var channels = new List<IncidentChannelResult>
            {
                new("System", IncidentQueryStatus.Success, null, 1),
                new("Application", IncidentQueryStatus.Error, "事件日志查询失败。", 0),
            };
            var result = new IncidentQueryResult(
                [SessionIncidentStoreTests.MakeIncident(0)], channels)
            {
                Status = IncidentQueryStatus.Partial,
            };
            var source = new FakeIncidentSource { ResultToReturn = result };
            var service = CreateService(source);
            var session = MakeSession(T(0), T(10));

            var envelope = await service.CaptureAsync(session);

            Assert.Equal(IncidentQueryStatus.Partial, envelope.QueryStatus);
            Assert.Equal(2, envelope.Channels.Count);
            // 查询失败状态也是 deterministic data-quality information：照常落盘。
            var store = new SessionIncidentStore(_temp.FullPath);
            var reloaded = store.Load(session.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(IncidentQueryStatus.Partial, reloaded!.QueryStatus);
        }

        [Fact]
        public async Task EmptyIncidentResult_Still_Saves_Envelope()
        {
            var source = new FakeIncidentSource();
            var service = CreateService(source);
            var session = MakeSession(T(0), T(5));

            var envelope = await service.CaptureAsync(session);

            Assert.Equal(IncidentQueryStatus.Success, envelope.QueryStatus);
            Assert.Empty(envelope.Incidents);
            var store = new SessionIncidentStore(_temp.FullPath);
            Assert.NotNull(store.Load(session.Id));
        }

        [Fact]
        public async Task SourceException_Propagates_And_NoIncidentsJsonIsWritten()
        {
            var source = new FakeIncidentSource
            {
                ExceptionToThrow = new InvalidOperationException("event log exploded"),
            };
            var service = CreateService(source);
            var session = MakeSession(T(0), T(5));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CaptureAsync(session));

            Assert.False(File.Exists(Path.Combine(_temp.FullPath, session.Id, "incidents.json")));
        }

        [Fact]
        public async Task EvidenceIds_AreConsumed_AsIs_NoRemapping()
        {
            var incidents = new List<WindowsIncident>
            {
                SessionIncidentStoreTests.MakeIncident(0) with { EvidenceId = "incident:0001" },
                SessionIncidentStoreTests.MakeIncident(1) with { EvidenceId = "incident:0002" },
            };
            var source = new FakeIncidentSource
            {
                ResultToReturn = new IncidentQueryResult(incidents, []),
            };
            var service = CreateService(source);
            var session = MakeSession(T(0), T(5));

            var envelope = await service.CaptureAsync(session);

            // correlation 只消费 IncidentQueryResult：EvidenceId 原样保留。
            Assert.Equal(["incident:0001", "incident:0002"], envelope.Incidents.Select(i => i.EvidenceId).ToArray());
        }

        private sealed class FakeIncidentSource : IIncidentSource
        {
            public IncidentQuery? LastQuery { get; private set; }

            public IncidentQueryResult ResultToReturn { get; set; } =
                new IncidentQueryResult([], [new IncidentChannelResult("System", IncidentQueryStatus.Success, null, 0)]);

            public Exception? ExceptionToThrow { get; set; }

            public Task<IncidentQueryResult> QueryAsync(
                IncidentQuery query, CancellationToken cancellationToken = default)
            {
                LastQuery = query;
                if (ExceptionToThrow is not null)
                {
                    throw ExceptionToThrow;
                }

                return Task.FromResult(ResultToReturn);
            }
        }
    }
}
