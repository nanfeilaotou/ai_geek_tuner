using System.IO;
using System.Text.Json;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.SessionAnalysis
{
    /// <summary>
    /// Gate L（builder/reducer）：telemetry-only、有界入选、WER 防挤占、
    /// 类别计数保全、temporal relation、确定性、incidents.json 不被修改。
    /// </summary>
    public sealed class DiagnosticEvidenceContextBuilderTests : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private static readonly DateTimeOffset T0 =
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

        private static TelemetrySessionAnalyzer.TelemetryAnalysisContext Telemetry() =>
            new(
                new TelemetrySessionAnalyzer.AnalysisMetadata("sid", T0, TimeSpan.FromSeconds(30), 15, 2000),
                [new TelemetrySessionAnalyzer.AnalysisSource("HwInfo", "Ready", 3)],
                [new TelemetrySessionAnalyzer.AnalysisStatistic(
                    "stat:cpu:cpu.package.temperature", "cpu", "cpu.package.temperature", "Celsius",
                    15, 100, 70, 72, 74, 74)],
                []);

        private static WindowsIncident Incident(
            string evidenceId,
            IncidentCategory category,
            DateTimeOffset? at = null,
            IncidentSeverity severity = IncidentSeverity.Error) => new(
            OccurredAtUtc: at ?? T0.AddSeconds(10),
            Category: category,
            Severity: severity,
            ProviderName: "TestProvider",
            EventId: 41,
            Channel: "System",
            RecordId: Random.Shared.NextInt64(1, long.MaxValue),
            Summary: "摘要 " + evidenceId,
            Details: "不应进入 AI 的长文本",
            EvidenceId: evidenceId);

        private static SessionIncidentEnvelope Envelope(
            IReadOnlyList<WindowsIncident> incidents,
            IncidentQueryStatus status = IncidentQueryStatus.Success) => new(
            SchemaVersion: 1,
            SessionId: "sid",
            QueriedAtUtc: T0.AddMinutes(1),
            WindowStartUtc: T0.AddSeconds(-30),
            WindowEndUtc: T0.AddSeconds(60),
            PreBuffer: TimeSpan.FromSeconds(30),
            PostBuffer: TimeSpan.FromSeconds(30),
            QueryStatus: status,
            Channels: [new IncidentChannelResult("System", status, null, incidents.Count)],
            Incidents: incidents);

        [Fact]
        public void TelemetryOnlySession_NoIncidentsFile_ProducesTelemetryOnlyContext()
        {
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), incidents: null);

            Assert.Null(context.WindowsIncidents);
            Assert.False(context.Metadata.IncidentEvidencePresent);
            Assert.Equal("sid", context.Metadata.SessionId);
            Assert.Equal("sid", context.Telemetry.Metadata.SessionId);
        }

        [Fact]
        public void EmptyIncidents_Still_ProducePresentIncidentEvidence()
        {
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope([]));

            Assert.NotNull(context.WindowsIncidents);
            Assert.True(context.Metadata.IncidentEvidencePresent);
            Assert.Equal(0, context.WindowsIncidents!.TotalIncidentCount);
            Assert.Equal(0, context.WindowsIncidents.IncludedIncidentCount);
            Assert.Empty(context.WindowsIncidents.Incidents);
        }

        [Fact]
        public void OneValidIncident_IsIncluded_WithOriginalEvidenceId_AndMappedFields()
        {
            var incident = Incident("incident:0001", IncidentCategory.UnexpectedShutdown);
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope([incident]));

            var item = Assert.Single(context.WindowsIncidents!.Incidents);
            Assert.Equal("incident:0001", item.EvidenceId); // Gate E：原样保留
            Assert.Equal(IncidentCategory.UnexpectedShutdown, item.Category);
            Assert.Equal(IncidentSeverity.Error, item.Severity);
            Assert.Equal("TestProvider", item.ProviderName);
            Assert.Equal(41, item.EventId);
            Assert.Equal("摘要 incident:0001", item.Summary);
            Assert.Equal(10_000, item.OffsetFromSessionStartMs); // T0+10s - T0
            Assert.Equal(IncidentTemporalRelation.WithinSession, item.TemporalRelation);
        }

        [Fact]
        public void MultipleIncidents_Keep_Original_EvidenceIds_And_OrderSelection()
        {
            var incidents = new List<WindowsIncident>
            {
                Incident("incident:0003", IncidentCategory.WindowsErrorReporting),
                Incident("incident:0001", IncidentCategory.HardwareError),
                Incident("incident:0002", IncidentCategory.BugCheck),
            };
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));

            Assert.All(context.WindowsIncidents!.Incidents, i => Assert.StartsWith("incident:", i.EvidenceId));
            // 优先级：HardwareError → BugCheck → WER（EvidenceId 不重编号）。
            Assert.Equal(
                ["incident:0001", "incident:0002", "incident:0003"],
                context.WindowsIncidents.Incidents.Select(i => i.EvidenceId).ToArray());
        }

        [Fact]
        public void WerSpam_IsBounded_ByCategoryCap_And_CountsPreserveTotal()
        {
            var incidents = new List<WindowsIncident>();
            for (var i = 1; i <= 30; i++)
            {
                incidents.Add(Incident($"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}",
                    IncidentCategory.WindowsErrorReporting));
            }

            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));
            var evidence = context.WindowsIncidents!;

            Assert.Equal(30, evidence.TotalIncidentCount);
            Assert.Equal(DiagnosticEvidenceContextBuilder.WindowsErrorReportingCap, evidence.IncludedIncidentCount);
            Assert.Equal(30 - evidence.IncludedIncidentCount, evidence.OmittedIncidentCount);
            Assert.Equal(30, evidence.CategoryCounts.Single(c => c.Category == IncidentCategory.WindowsErrorReporting).Count);
        }

        [Fact]
        public void HighValueIncident_Outranks_WerSpam()
        {
            var incidents = new List<WindowsIncident>();
            for (var i = 1; i <= 30; i++)
            {
                incidents.Add(Incident($"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}",
                    IncidentCategory.WindowsErrorReporting, at: T0.AddSeconds(i)));
            }
            incidents.Add(Incident("incident:9001", IncidentCategory.HardwareError, at: T0.AddSeconds(5)));

            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));
            var evidence = context.WindowsIncidents!;

            Assert.Equal("incident:9001", evidence.Incidents[0].EvidenceId); // WHEA 永远排最前
            Assert.True(evidence.Incidents.Count <= DiagnosticEvidenceContextBuilder.MaxIncludedIncidents);
        }

        [Fact]
        public void TotalIncluded_IsBounded_ToMaxIncludedIncidents()
        {
            var incidents = new List<WindowsIncident>();
            var categories = new[]
            {
                IncidentCategory.HardwareError, IncidentCategory.BugCheck, IncidentCategory.DisplayDriver,
                IncidentCategory.Storage, IncidentCategory.UnexpectedShutdown,
            };
            for (var i = 1; i <= 60; i++)
            {
                incidents.Add(Incident(
                    $"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}",
                    categories[i % categories.Length],
                    at: T0.AddSeconds(i)));
            }

            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));

            Assert.Equal(DiagnosticEvidenceContextBuilder.MaxIncludedIncidents,
                context.WindowsIncidents!.IncludedIncidentCount);
            Assert.Equal(60, context.WindowsIncidents.TotalIncidentCount);
        }

        [Fact]
        public void CategoryCounts_Preserve_FullEvidenceInformation()
        {
            var incidents = new List<WindowsIncident>
            {
                Incident("incident:0001", IncidentCategory.WindowsErrorReporting, at: T0.AddSeconds(1)),
                Incident("incident:0002", IncidentCategory.WindowsErrorReporting, at: T0.AddSeconds(2)),
                Incident("incident:0003", IncidentCategory.WindowsErrorReporting, at: T0.AddSeconds(3)),
                Incident("incident:0004", IncidentCategory.WindowsErrorReporting, at: T0.AddSeconds(4)),
                Incident("incident:0005", IncidentCategory.WindowsErrorReporting, at: T0.AddSeconds(5)),
                Incident("incident:0006", IncidentCategory.HardwareError, at: T0.AddSeconds(6)),
            };

            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));
            var evidence = context.WindowsIncidents!;

            Assert.Equal(6, evidence.TotalIncidentCount);          // 完整证据信息
            Assert.Equal(5, evidence.CategoryCounts.Single(c => c.Category == IncidentCategory.WindowsErrorReporting).Count);
            Assert.Equal(1, evidence.CategoryCounts.Single(c => c.Category == IncidentCategory.HardwareError).Count);
            Assert.Equal(5, evidence.IncludedIncidentCount);       // WHEA 1 + WER cap 4
            Assert.Equal(1, evidence.OmittedIncidentCount);
        }

        [Theory]
        [InlineData(IncidentQueryStatus.Partial)]
        [InlineData(IncidentQueryStatus.PermissionDenied)]
        [InlineData(IncidentQueryStatus.Unavailable)]
        public void QueryStatus_Enters_Context_AsCoverageLimitation(IncidentQueryStatus status)
        {
            var context = DiagnosticEvidenceContextBuilder.Build(
                Telemetry(), Envelope([Incident("incident:0001", IncidentCategory.BugCheck)], status));

            Assert.NotNull(context.WindowsIncidents);
            Assert.Equal(status, context.WindowsIncidents!.QueryStatus);
        }

        [Theory]
        [InlineData(-10, IncidentTemporalRelation.BeforeSession)]   // start 前 30s buffer 区域
        [InlineData(10, IncidentTemporalRelation.WithinSession)]
        [InlineData(35, IncidentTemporalRelation.AfterSession)]     // completed 后 30s buffer 区域
        public void TemporalRelation_Describes_Position_Relative_To_Session(
            int offsetSeconds, IncidentTemporalRelation expected)
        {
            var incident = Incident("incident:0001", IncidentCategory.BugCheck,
                at: T0.AddSeconds(offsetSeconds));
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope([incident]));

            Assert.Equal(expected, Assert.Single(context.WindowsIncidents!.Incidents).TemporalRelation);
        }

        [Fact]
        public void Reducer_IsDeterministic()
        {
            var incidents = new List<WindowsIncident>();
            for (var i = 1; i <= 40; i++)
            {
                incidents.Add(Incident(
                    $"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}",
                    (IncidentCategory)(i % 9),
                    at: T0.AddSeconds(i),
                    severity: (IncidentSeverity)(i % 4)));
            }

            var first = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));
            var second = DiagnosticEvidenceContextBuilder.Build(Telemetry(), Envelope(incidents));

            Assert.Equal(
                first.WindowsIncidents!.Incidents.Select(i => i.EvidenceId),
                second.WindowsIncidents!.Incidents.Select(i => i.EvidenceId));
        }

        [Fact]
        public void Reducer_DoesNotModify_IncidentsJson()
        {
            var store = new SessionIncidentStore(_temp.FullPath);
            var incidents = new List<WindowsIncident>();
            for (var i = 1; i <= 25; i++)
            {
                incidents.Add(new WindowsIncident(
                    OccurredAtUtc: T0.AddSeconds(i),
                    Category: IncidentCategory.WindowsErrorReporting,
                    Severity: IncidentSeverity.Information,
                    ProviderName: "Windows Error Reporting",
                    EventId: 1001,
                    Channel: "Application",
                    RecordId: i,
                    Summary: "WER " + i,
                    Details: "长 Details 不进 AI",
                    EvidenceId: $"incident:{i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}"));
            }
            var envelope = new SessionIncidentEnvelope(
                1, "sid", T0.AddMinutes(1), T0.AddSeconds(-30), T0.AddSeconds(60),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
                IncidentQueryStatus.Success,
                [new IncidentChannelResult("Application", IncidentQueryStatus.Success, null, 25)],
                incidents);
            store.Save(envelope);

            var loaded = store.Load("sid");
            Assert.NotNull(loaded);
            var context = DiagnosticEvidenceContextBuilder.Build(Telemetry(), loaded);

            Assert.True(context.WindowsIncidents!.Incidents.Count < 25); // AI 视图被压缩
            var after = File.ReadAllText(store.PathOf("sid"));
            var reloaded = store.Load("sid");
            Assert.NotNull(reloaded);
            Assert.Equal(25, reloaded!.Incidents.Count);                 // evidence store 完整
            Assert.All(reloaded.Incidents, i => Assert.NotNull(i.Details)); // incidents.json 原样
        }
    }
}
