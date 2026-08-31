using System.IO;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.Incidents
{
    /// <summary>
    /// Gate G：incidents.json 持久化。roundtrip / 覆盖 / 损坏 / 缺失 /
    /// 与 session.json、analysis.json 的证据隔离。
    /// </summary>
    public sealed class SessionIncidentStoreTests : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly SessionIncidentStore _store;

        public SessionIncidentStoreTests()
        {
            _store = new SessionIncidentStore(_temp.FullPath);
        }

        public void Dispose() => _temp.Dispose();

        private static DateTimeOffset T(int minutes) =>
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        internal static WindowsIncident MakeIncident(int index) => new(
            OccurredAtUtc: T(index),
            Category: index % 2 == 0 ? IncidentCategory.UnexpectedShutdown : IncidentCategory.ApplicationCrash,
            Severity: index % 2 == 0 ? IncidentSeverity.Critical : IncidentSeverity.Error,
            ProviderName: "Microsoft-Windows-Kernel-Power",
            EventId: 41 + index,
            Channel: index % 2 == 0 ? "System" : "Application",
            RecordId: 1000 + index,
            Summary: "事件 " + index,
            Details: index == 0 ? null : "详细信息 " + index,
            EvidenceId: $"incident:{(index + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}");

        internal static SessionIncidentEnvelope MakeEnvelope(
            string sessionId,
            IncidentQueryStatus status = IncidentQueryStatus.Success,
            IReadOnlyList<WindowsIncident>? incidents = null,
            IReadOnlyList<IncidentChannelResult>? channels = null) => new(
            SchemaVersion: 1,
            SessionId: sessionId,
            QueriedAtUtc: T(60),
            WindowStartUtc: T(0),
            WindowEndUtc: T(30),
            PreBuffer: TimeSpan.FromSeconds(30),
            PostBuffer: TimeSpan.FromSeconds(30),
            QueryStatus: status,
            Channels: channels ?? new List<IncidentChannelResult>
            {
                new("System", status, null, incidents?.Count ?? 0),
            },
            Incidents: incidents ?? new List<WindowsIncident> { MakeIncident(0), MakeIncident(1) });

        [Fact]
        public void SaveThenLoad_RoundTrips_AllIncidentFields_EvidenceId_And_Order()
        {
            var envelope = MakeEnvelope("s-roundtrip");

            _store.Save(envelope);

            Assert.True(File.Exists(_store.PathOf("s-roundtrip")));
            var loaded = _store.Load("s-roundtrip");
            Assert.NotNull(loaded);

            Assert.Equal(1, loaded!.SchemaVersion);
            Assert.Equal("s-roundtrip", loaded.SessionId);
            Assert.Equal(envelope.QueriedAtUtc, loaded.QueriedAtUtc);
            Assert.Equal(envelope.WindowStartUtc, loaded.WindowStartUtc);
            Assert.Equal(envelope.WindowEndUtc, loaded.WindowEndUtc);
            Assert.Equal(TimeSpan.FromSeconds(30), loaded.PreBuffer);
            Assert.Equal(TimeSpan.FromSeconds(30), loaded.PostBuffer);

            Assert.Equal(2, loaded.Incidents.Count);
            for (var i = 0; i < envelope.Incidents.Count; i++)
            {
                var expected = envelope.Incidents[i];
                var actual = loaded.Incidents[i];
                Assert.Equal(expected.EvidenceId, actual.EvidenceId); // 不重新编号
                Assert.Equal(expected.OccurredAtUtc, actual.OccurredAtUtc);
                Assert.Equal(expected.Category, actual.Category);
                Assert.Equal(expected.Severity, actual.Severity);
                Assert.Equal(expected.ProviderName, actual.ProviderName);
                Assert.Equal(expected.EventId, actual.EventId);
                Assert.Equal(expected.Channel, actual.Channel);
                Assert.Equal(expected.RecordId, actual.RecordId);
                Assert.Equal(expected.Summary, actual.Summary);
                Assert.Equal(expected.Details, actual.Details);
            }
        }

        [Fact]
        public void SaveThenLoad_Preserves_StableIncidentOrder()
        {
            var incidents = new List<WindowsIncident>();
            for (var i = 4; i >= 0; i--)
            {
                incidents.Add(MakeIncident(i));
            }

            _store.Save(MakeEnvelope("s-order", incidents: incidents));
            var loaded = _store.Load("s-order");

            Assert.NotNull(loaded);
            // 保存顺序原样保留：不排序、不重编号。
            Assert.Equal(
                incidents.Select(i => i.EvidenceId).ToArray(),
                loaded!.Incidents.Select(i => i.EvidenceId).ToArray());
            Assert.Equal(
                incidents.Select(i => i.OccurredAtUtc).ToArray(),
                loaded.Incidents.Select(i => i.OccurredAtUtc).ToArray());
        }

        [Fact]
        public void SaveThenLoad_PartialStatus_RoundTrips()
        {
            _store.Save(MakeEnvelope("s-partial", status: IncidentQueryStatus.Partial));
            var loaded = _store.Load("s-partial");

            Assert.NotNull(loaded);
            Assert.Equal(IncidentQueryStatus.Partial, loaded!.QueryStatus);
        }

        [Fact]
        public void SaveThenLoad_PermissionDenied_And_Unavailable_Channels_RoundTrip()
        {
            var channels = new List<IncidentChannelResult>
            {
                new("System", IncidentQueryStatus.PermissionDenied, "没有读取该事件日志的权限。", 0),
                new("Application", IncidentQueryStatus.Unavailable, "该事件日志在本机不可用。", 0),
            };

            _store.Save(MakeEnvelope(
                "s-denied", status: IncidentQueryStatus.PermissionDenied,
                incidents: [], channels: channels));

            var loaded = _store.Load("s-denied");
            Assert.NotNull(loaded);
            Assert.Equal(IncidentQueryStatus.PermissionDenied, loaded!.QueryStatus);
            Assert.Equal(2, loaded.Channels.Count);
            Assert.Equal("System", loaded.Channels[0].Channel);
            Assert.Equal(IncidentQueryStatus.PermissionDenied, loaded.Channels[0].Status);
            Assert.Equal("没有读取该事件日志的权限。", loaded.Channels[0].StatusMessage);
            Assert.Equal("Application", loaded.Channels[1].Channel);
            Assert.Equal(IncidentQueryStatus.Unavailable, loaded.Channels[1].Status);
        }

        [Fact]
        public void SaveThenLoad_EmptyIncidents_StillPersistsEnvelope()
        {
            _store.Save(MakeEnvelope("s-empty", incidents: []));

            var loaded = _store.Load("s-empty");
            Assert.NotNull(loaded);
            Assert.Empty(loaded!.Incidents);
            Assert.Equal(IncidentQueryStatus.Success, loaded.QueryStatus);
        }

        [Fact]
        public void Save_OverwritesExistingIncidentsJson()
        {
            var first = MakeEnvelope("s-overwrite", incidents: [MakeIncident(0)]);
            var second = MakeEnvelope("s-overwrite", incidents: [MakeIncident(0), MakeIncident(1)]);

            _store.Save(first);
            _store.Save(second);

            var loaded = _store.Load("s-overwrite");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Incidents.Count);
            Assert.Equal(second.QueriedAtUtc, loaded.QueriedAtUtc);
        }

        [Fact]
        public void Load_CorruptIncidentsJson_ReturnsNull()
        {
            var directory = Path.Combine(_temp.FullPath, "s-corrupt");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "incidents.json"), "{ not valid json !!!");

            Assert.Null(_store.Load("s-corrupt"));
        }

        [Fact]
        public void Load_MissingIncidentsJson_ReturnsNull()
        {
            // 旧 Session 没有 incidents.json 是合法状态（不 backfill，不报错）。
            Assert.Null(_store.Load("no-such-session"));
        }

        [Fact]
        public void SaveAndLoad_DoNotTouch_SessionJson_Or_AnalysisJson()
        {
            var sessionStore = new TelemetrySessionStore(_temp.FullPath);
            var analysisStore = new SessionAnalysisStore(_temp.FullPath);
            var session = TelemetryRecordingSession.Start(1000, T(0)) with
            {
                Status = RecordingStatus.Completed,
                CompletedAtUtc = T(1),
            };
            sessionStore.Save(session);
            var sessionJsonPath = sessionStore.PathOf(session.Id);
            var analysisJsonPath = Path.Combine(_temp.FullPath, session.Id, "analysis.json");
            File.WriteAllText(analysisJsonPath, "{\"marker\":\"keep-me\"}");

            var sessionJsonBefore = File.ReadAllText(sessionJsonPath);
            var analysisJsonBefore = File.ReadAllText(analysisJsonPath);

            _store.Save(MakeEnvelope(session.Id));
            Assert.NotNull(_store.Load(session.Id));

            Assert.Equal(sessionJsonBefore, File.ReadAllText(sessionJsonPath));
            Assert.Equal(analysisJsonBefore, File.ReadAllText(analysisJsonPath));
        }
    }
}
