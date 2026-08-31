using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.Incidents;
using Xunit;

namespace AIGeekTuner.Tests.Services.Incidents
{
    /// <summary>Gate G（Mapping / Normalization）：Provider+EventId 规则与归一化语义。</summary>
    public sealed class WindowsIncidentMapperTests
    {
        private static RawWindowsEvent Raw(
            string provider, int eventId, int? level = 2,
            long? recordId = 42, string? message = null,
            DateTimeOffset? time = null, string channel = "System") =>
            new(
                TimeCreatedUtc: time ?? new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero),
                ProviderName: provider,
                EventId: eventId,
                Level: level,
                Channel: channel,
                RecordId: recordId,
                Message: message);

        [Fact]
        public void KernelPower41_MapsToUnexpectedShutdown_WithNeutralSummary()
        {
            var incident = WindowsIncidentMapper.Map(Raw("Kernel-Power", 41, level: 1));

            Assert.NotNull(incident);
            Assert.Equal(IncidentCategory.UnexpectedShutdown, incident!.Category);
            Assert.Equal(IncidentSeverity.Critical, incident.Severity);
            // 语义红线：41 绝不解释为电源/CPU/PSU 故障。
            Assert.DoesNotContain("电源故障", incident.Summary);
            Assert.DoesNotContain("PSU", incident.Summary);
            Assert.DoesNotContain("CPU 故障", incident.Summary);
        }

        [Fact]
        public void EventLog6008_MapsToUnexpectedShutdown()
        {
            var incident = WindowsIncidentMapper.Map(Raw("EventLog", 6008));

            Assert.NotNull(incident);
            Assert.Equal(IncidentCategory.UnexpectedShutdown, incident!.Category);
        }

        [Fact]
        public void WerSystemErrorReporting1001_MapsToBugCheck()
        {
            var incident = WindowsIncidentMapper.Map(Raw(
                "Microsoft-Windows-WER-SystemErrorReporting", 1001,
                message: "计算机已从检测错误后重新启动。"));

            Assert.NotNull(incident);
            Assert.Equal(IncidentCategory.BugCheck, incident!.Category);
            Assert.Contains("检测错误", incident.Summary);
        }

        [Fact]
        public void WheaLogger_AnyEventId_MapsToHardwareError()
        {
            foreach (var id in new[] { 1, 3, 18, 47 })
            {
                var incident = WindowsIncidentMapper.Map(Raw("Microsoft-Windows-WHEA-Logger", id));
                Assert.NotNull(incident);
                Assert.Equal(IncidentCategory.HardwareError, incident!.Category);
            }
        }

        [Fact]
        public void Display4101_MapsToDisplayDriver_ButUnknownDisplayIdsDoNot()
        {
            var tdr = WindowsIncidentMapper.Map(Raw("Display", 4101));
            Assert.NotNull(tdr);
            Assert.Equal(IncidentCategory.DisplayDriver, tdr!.Category);

            Assert.Null(WindowsIncidentMapper.Map(Raw("Display", 4107)));
        }

        [Fact]
        public void DiskErrors_MapsToStorage()
        {
            foreach (var id in new[] { 7, 51, 153 })
            {
                var incident = WindowsIncidentMapper.Map(Raw("disk", id));
                Assert.NotNull(incident);
                Assert.Equal(IncidentCategory.Storage, incident!.Category);
            }
        }

        [Fact]
        public void ApplicationProviders_MapByExactCombination()
        {
            var crash = WindowsIncidentMapper.Map(Raw(
                "Application Error", 1000, channel: "Application"));
            var hang = WindowsIncidentMapper.Map(Raw(
                "Application Hang", 1002, channel: "Application"));
            var wer = WindowsIncidentMapper.Map(Raw(
                "Windows Error Reporting", 1001, channel: "Application"));

            Assert.Equal(IncidentCategory.ApplicationCrash, crash!.Category);
            Assert.Equal(IncidentCategory.ApplicationHang, hang!.Category);
            Assert.Equal(IncidentCategory.WindowsErrorReporting, wer!.Category);
        }

        [Fact]
        public void WrongEventId_WithKnownProvider_DoesNotMap()
        {
            // 已知 provider + 未知 ID → 不映射（宁缺毋滥）。
            Assert.Null(WindowsIncidentMapper.Map(Raw("Kernel-Power", 1074)));
            Assert.Null(WindowsIncidentMapper.Map(Raw("Application Error", 1001)));
        }

        [Fact]
        public void UnknownProvider_IsIgnored()
        {
            Assert.Null(WindowsIncidentMapper.Map(Raw("Totally-Unknown-Provider", 999)));
        }

        [Fact]
        public void Severity_MapsFromEventLevel()
        {
            Assert.Equal(IncidentSeverity.Critical, WindowsIncidentMapper.MapSeverity(1));
            Assert.Equal(IncidentSeverity.Error, WindowsIncidentMapper.MapSeverity(2));
            Assert.Equal(IncidentSeverity.Warning, WindowsIncidentMapper.MapSeverity(3));
            Assert.Equal(IncidentSeverity.Information, WindowsIncidentMapper.MapSeverity(4));
            Assert.Equal(IncidentSeverity.Information, WindowsIncidentMapper.MapSeverity(0));
            Assert.Equal(IncidentSeverity.Information, WindowsIncidentMapper.MapSeverity(null));
            Assert.Equal(IncidentSeverity.Information, WindowsIncidentMapper.MapSeverity(99));
        }

        [Fact]
        public void MessageFormatFailure_UsesProviderFallback()
        {
            var incident = WindowsIncidentMapper.Map(Raw(
                "Microsoft-Windows-WHEA-Logger", 18, message: null));

            Assert.NotNull(incident);
            Assert.Equal("Microsoft-Windows-WHEA-Logger（事件 18）", incident!.Summary);
            Assert.Null(incident.Details);
        }

        [Fact]
        public void Details_TruncatedAt2000Chars()
        {
            var longMessage = new string('x', 5000);
            var incident = WindowsIncidentMapper.Map(Raw(
                "Application Error", 1000, message: longMessage,
                channel: "Application"));

            Assert.NotNull(incident);
            Assert.NotNull(incident!.Details);
            Assert.Equal(WindowsIncidentMapper.MaxDetailsLength, incident.Details!.Length);
            Assert.True(incident.Summary.Length <= 300);
        }

        [Fact]
        public void Time_IsNormalizedToUtc()
        {
            var localTime = new DateTimeOffset(2026, 2, 1, 18, 0, 0, TimeSpan.FromHours(8));
            var incident = WindowsIncidentMapper.Map(Raw("Kernel-Power", 41, time: localTime));

            Assert.NotNull(incident);
            Assert.Equal(TimeSpan.Zero, incident!.OccurredAtUtc.Offset);
            Assert.Equal(10, incident.OccurredAtUtc.Hour);
        }
    }
}
