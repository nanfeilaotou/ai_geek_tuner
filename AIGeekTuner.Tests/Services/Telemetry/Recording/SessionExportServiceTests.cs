using System.IO.Compression;
using System.IO;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording;

public sealed class SessionExportServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TelemetrySessionStore _sessions;
    private readonly SessionAnalysisStore _analysis;
    private readonly SessionIncidentStore _incidents;
    private readonly SessionExportService _service;

    public SessionExportServiceTests()
    {
        _sessions = new TelemetrySessionStore(_temp.Combine("Sessions"));
        _analysis = new SessionAnalysisStore(_temp.Combine("Sessions"));
        _incidents = new SessionIncidentStore(_temp.Combine("Sessions"));
        _service = new SessionExportService(_sessions, _analysis, _incidents);
    }

    [Fact]
    public async Task MarkdownExport_UsesPersistedSummaryAndIncludesRequiredSections()
    {
        var session = CreateSession();
        _sessions.Save(session);
        var path = _temp.Combine("report.md");

        await _service.ExportReportAsync(session.Id, path);

        var markdown = await File.ReadAllTextAsync(path);
        Assert.Contains("# AIGeekTuner 录制报告", markdown);
        Assert.Contains("## 1. 基本信息", markdown);
        Assert.Contains("## 2. 硬件摘要", markdown);
        Assert.Contains("## 3. Telemetry 统计", markdown);
        Assert.Contains("P95", markdown);
        Assert.Contains("CPU-TEST", markdown);
        Assert.Contains("## 4. 关键变化 / Recording Events", markdown);
        Assert.Contains("## 5. Windows Incident Evidence", markdown);
        Assert.Contains("## 6. AI Analysis", markdown);
        Assert.Contains("## 7. 数据说明", markdown);
        Assert.Contains("correlation", markdown);
    }

    [Fact]
    public async Task MarkdownExport_WithoutAnalysisOrIncidents_IsExplicit()
    {
        var session = CreateSession();
        _sessions.Save(session);
        var path = _temp.Combine("no-analysis.md");

        await _service.ExportReportAsync(session.Id, path);

        var markdown = await File.ReadAllTextAsync(path);
        Assert.Contains("本次时间窗口内未记录到相关事件", markdown);
        Assert.Contains("尚未生成 AI 分析", markdown);
    }

    [Fact]
    public async Task MarkdownExport_IncludesIncidentAndAnalysisEvidence()
    {
        var session = CreateSession();
        _sessions.Save(session);
        _incidents.Save(new SessionIncidentEnvelope(
            1,
            session.Id,
            session.StartedAtUtc,
            session.StartedAtUtc,
            session.CompletedAtUtc!.Value,
            TimeSpan.Zero,
            TimeSpan.Zero,
            IncidentQueryStatus.Success,
            [new IncidentChannelResult("System", IncidentQueryStatus.Success, null, 1)],
            [new WindowsIncident(
                session.StartedAtUtc,
                IncidentCategory.HardwareError,
                IncidentSeverity.Error,
                "Microsoft-Windows-WHEA-Logger",
                17,
                "System",
                42,
                "硬件事件测试",
                "details",
                "incident:0001")]));
        _analysis.Save(new SessionAnalysisEnvelope(
            1,
            session.Id,
            session.StartedAtUtc,
            "test-model",
            123,
            false,
            new SessionAnalysisResult(
                "分析摘要",
                SessionOverallAssessment.Attention,
                0.8,
                [new SessionFinding(
                    "发现标题",
                    SessionFindingCategory.Stability,
                    "需要关注",
                    "解释",
                    ["stat:cpu:cpu.package.temperature", "incident:0001"])],
                [new SessionRecommendation("建议操作")],
                ["数据限制"],
                "语音摘要文本"),
            "{}",
            "provider-id",
            "Provider"));
        var path = _temp.Combine("analysis.md");

        await _service.ExportReportAsync(session.Id, path);

        var markdown = await File.ReadAllTextAsync(path);
        Assert.Contains("incident:0001", markdown);
        Assert.Contains("分析摘要", markdown);
        Assert.Contains("Evidence IDs", markdown);
        Assert.Contains("语音摘要文本", markdown);
    }

    [Fact]
    public async Task EvidenceZip_ContainsOnlyExpectedPersistedFilesAndExcludesVoice()
    {
        var session = CreateSession();
        _sessions.Save(session);
        _analysis.Save(new SessionAnalysisEnvelope(
            1,
            session.Id,
            session.StartedAtUtc,
            "model",
            1,
            false,
            new SessionAnalysisResult(
                "summary",
                SessionOverallAssessment.Normal,
                0.5,
                [],
                [],
                [],
                "spoken"),
            "{}"));
        _analysis.SaveVoiceWav(session.Id, TestWavBytes());
        var path = _temp.Combine("evidence.zip");

        await _service.ExportEvidencePackageAsync(session.Id, path);

        using var archive = ZipFile.OpenRead(path);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("README.md", names);
        Assert.Contains("report.md", names);
        Assert.Contains("session.json", names);
        Assert.Contains("analysis.json", names);
        Assert.DoesNotContain("incidents.json", names);
        Assert.DoesNotContain("voice.wav", names);
        Assert.DoesNotContain("credentials.json", names);
        Assert.DoesNotContain("settings.json", names);
        Assert.Contains("Schema：", ReadEntry(archive, "README.md"));
        Assert.Contains("AI analysis 与原始 telemetry 是分离层", ReadEntry(archive, "README.md"));
    }

    [Fact]
    public async Task ExportFailure_DoesNotLeavePartialTarget()
    {
        var path = _temp.Combine("missing.md");

        await Assert.ThrowsAsync<SessionExportException>(
            () => _service.ExportReportAsync("missing-session", path));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_temp.FullPath, "*.tmp", SearchOption.AllDirectories));
    }

    private static TelemetryRecordingSession CreateSession()
    {
        var started = new DateTimeOffset(2026, 9, 6, 2, 30, 0, TimeSpan.Zero);
        var cpu = TelemetryDeviceIdentity.Cpu("CPU-TEST");
        var gpu = TelemetryDeviceIdentity.GpuByIndex(0, "GPU-TEST");
        var samples = new[]
        {
            new TelemetrySample(
                1,
                started.AddSeconds(1),
                4,
                [
                    new TelemetryReading(
                        TelemetryMetricKey.CpuPackageTemperature,
                        70,
                        TelemetryUnit.Celsius,
                        cpu,
                        TelemetrySourceKind.HwInfo,
                        "cpu.temp",
                        null,
                        started.AddSeconds(1)),
                    new TelemetryReading(
                        TelemetryMetricKey.GpuCoreTemperature,
                        60,
                        TelemetryUnit.Celsius,
                        gpu,
                        TelemetrySourceKind.Aida64,
                        "gpu.temp",
                        null,
                        started.AddSeconds(1))
                ])
        };
        var summary = new TelemetrySessionSummary(
            TimeSpan.FromSeconds(5),
            1,
            2000,
            [new MetricSeriesStatistic(
                "cpu",
                "CPU-TEST",
                "cpu.package.temperature",
                "Celsius",
                1,
                100,
                70,
                70,
                70,
                70,
                70,
                70,
                started.AddSeconds(1),
                started.AddSeconds(1))],
            [new TelemetrySessionEvent(
                TelemetrySessionEventType.SignificantChange,
                started.AddSeconds(2),
                "HWiNFO",
                "cpu.package.temperature",
                "cpu",
                "70",
                "80",
                "温度变化测试")],
            [],
            [new TelemetrySourceReport(
                TelemetrySourceKind.HwInfo,
                TelemetrySourceStatus.Ready,
                "ready",
                2,
                started.AddSeconds(1),
                2,
                4)]);

        return new TelemetryRecordingSession(
            "session-export-test",
            started,
            started.AddSeconds(5),
            2000,
            RecordingStatus.Completed,
            samples,
            [],
            summary,
            summary.FinalSources);
    }

    private static byte[] TestWavBytes() =>
        "RIFF0000WAVE"u8.ToArray();

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose() => _temp.Dispose();
}
