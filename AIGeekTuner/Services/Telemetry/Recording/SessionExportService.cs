using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Incidents;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Telemetry.Recording;

public interface ISessionExportService
{
    Task<string> ExportReportAsync(
        string sessionId,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<string> ExportEvidencePackageAsync(
        string sessionId,
        string destinationPath,
        CancellationToken cancellationToken = default);
}

public sealed class SessionExportException : Exception
{
    public SessionExportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exports already persisted session facts. It never samples telemetry, calls
/// Windows Event Log, or invokes an AI/voice service.
/// </summary>
public sealed class SessionExportService : ISessionExportService
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    private readonly ITelemetrySessionStore _sessionStore;
    private readonly ISessionAnalysisStore _analysisStore;
    private readonly ISessionIncidentStore _incidentStore;

    public SessionExportService(
        ITelemetrySessionStore sessionStore,
        ISessionAnalysisStore analysisStore,
        ISessionIncidentStore incidentStore)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _analysisStore = analysisStore ?? throw new ArgumentNullException(nameof(analysisStore));
        _incidentStore = incidentStore ?? throw new ArgumentNullException(nameof(incidentStore));
    }

    public async Task<string> ExportReportAsync(
        string sessionId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bundle = LoadBundle(sessionId);
            await AtomicFileWriter.WriteTextAsync(
                destinationPath,
                BuildMarkdown(bundle),
                cancellationToken);
            return Path.GetFullPath(destinationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException)
        {
            throw new SessionExportException("录制报告导出失败，请检查目标文件夹权限。", exception);
        }
    }

    public async Task<string> ExportEvidencePackageAsync(
        string sessionId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bundle = LoadBundle(sessionId);
            var sessionPath = _sessionStore.PathOf(sessionId);
            if (!File.Exists(sessionPath))
            {
                throw new SessionExportException("找不到该录制会话的 session.json。" );
            }

            await WriteZipAtomicallyAsync(
                destinationPath,
                bundle,
                sessionPath,
                cancellationToken);
            return Path.GetFullPath(destinationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException)
        {
            throw new SessionExportException("证据包导出失败，请检查目标文件夹权限。", exception);
        }
    }

    internal static string BuildMarkdownForTests(
        TelemetryRecordingSession session,
        SessionIncidentEnvelope? incidents,
        SessionAnalysisEnvelope? analysis)
    {
        ArgumentNullException.ThrowIfNull(session);
        return BuildMarkdown(new SessionBundle(session, incidents, analysis));
    }

    private SessionBundle LoadBundle(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId != Path.GetFileName(sessionId)
            || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new SessionExportException("会话 ID 不能为空。" );
        }

        var session = _sessionStore.Load(sessionId);
        if (session is null)
        {
            throw new SessionExportException("找不到该录制会话或会话文件已损坏。" );
        }

        return new SessionBundle(
            session,
            _incidentStore.Load(sessionId),
            _analysisStore.Load(sessionId));
    }

    private static string BuildMarkdown(SessionBundle bundle)
    {
        var session = bundle.Session;
        // Completed sessions persist Summary. The analyzer is only a compatibility
        // fallback for older completed files which predate the Summary field.
        var summary = session.Summary ?? TelemetrySessionAnalyzer.Analyze(session);
        var builder = new StringBuilder();
        builder.AppendLine("# AIGeekTuner 录制报告");
        builder.AppendLine();
        AppendBasicInfo(builder, session, summary);
        AppendHardware(builder, session);
        AppendStatistics(builder, summary);
        AppendEvents(builder, summary);
        AppendIncidents(builder, bundle.Incidents);
        AppendAnalysis(builder, bundle.Analysis);
        AppendDataNotes(builder, summary);
        return builder.ToString();
    }

    private static void AppendBasicInfo(
        StringBuilder builder,
        TelemetryRecordingSession session,
        TelemetrySessionSummary summary)
    {
        builder.AppendLine("## 1. 基本信息");
        builder.AppendLine();
        builder.AppendLine($"- Session ID：{Inline(session.Id)}");
        builder.AppendLine($"- 状态：{session.Status}");
        builder.AppendLine($"- 开始时间（UTC）：{session.StartedAtUtc:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 结束时间（UTC）：{(session.CompletedAtUtc?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "未完成")}");
        builder.AppendLine($"- Duration：{summary.Duration:c}");
        builder.AppendLine($"- Sampling interval：{summary.RequestedIntervalMs} ms");
        builder.AppendLine($"- Sample count：{summary.SampleCount}");
        builder.AppendLine();
        builder.AppendLine("### 数据源状态");
        builder.AppendLine();
        if (summary.FinalSources.Count == 0)
        {
            builder.AppendLine("- 未记录到数据源状态。");
        }
        else
        {
            foreach (var source in summary.FinalSources)
            {
                var duration = source.ReadDurationMs.HasValue
                    ? $"，读取 {source.ReadDurationMs.Value} ms"
                    : string.Empty;
                builder.AppendLine(
                    $"- {Inline(source.Source.ToString())}：{source.Status}，{source.CanonicalReadingCount} 条 canonical 读数{duration}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendHardware(
        StringBuilder builder,
        TelemetryRecordingSession session)
    {
        builder.AppendLine("## 2. 硬件摘要");
        builder.AppendLine();

        var devices = session.Samples
            .SelectMany(sample => sample.Readings)
            .Select(reading => reading.Device)
            .GroupBy(device => device.DeviceKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        AppendDevices(builder, devices, TelemetryDeviceKind.Cpu, "CPU");
        AppendDevices(builder, devices, TelemetryDeviceKind.Gpu, "GPU");
        AppendDevices(builder, devices, TelemetryDeviceKind.Memory, "Memory");
        var other = devices
            .Where(device => device.Kind is not TelemetryDeviceKind.Cpu
                and not TelemetryDeviceKind.Gpu
                and not TelemetryDeviceKind.Memory)
            .Select(device => device.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        builder.AppendLine($"- 其它相关设备：{(other.Length == 0 ? "未知" : string.Join(" / ", other.Select(Inline)))}");
        builder.AppendLine();
    }

    private static void AppendDevices(
        StringBuilder builder,
        IReadOnlyList<TelemetryDeviceIdentity> devices,
        TelemetryDeviceKind kind,
        string label)
    {
        var names = devices
            .Where(device => device.Kind == kind)
            .Select(device => device.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        builder.AppendLine($"- {label}：{(names.Length == 0 ? "未知" : string.Join(" / ", names.Select(Inline)))}");
    }

    private static void AppendStatistics(StringBuilder builder, TelemetrySessionSummary summary)
    {
        builder.AppendLine("## 3. Telemetry 统计");
        builder.AppendLine();
        builder.AppendLine("| Device | Canonical metric | Min | Average | Max | P95 | Samples |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        if (summary.Statistics.Count == 0)
        {
            builder.AppendLine("| 未知 | 未记录 | — | — | — | — | 0 |");
        }
        else
        {
            foreach (var statistic in summary.Statistics)
            {
                builder.AppendLine(
                    $"| {Table(statistic.DeviceName)} | {Table(statistic.MetricKey)} | {statistic.Minimum.ToString("0.###", CultureInfo.InvariantCulture)} {Table(statistic.Unit)} | {statistic.Average.ToString("0.###", CultureInfo.InvariantCulture)} {Table(statistic.Unit)} | {statistic.Maximum.ToString("0.###", CultureInfo.InvariantCulture)} {Table(statistic.Unit)} | {statistic.P95.ToString("0.###", CultureInfo.InvariantCulture)} {Table(statistic.Unit)} | {statistic.SampleCount} |");
            }
        }

        builder.AppendLine();
    }

    private static void AppendEvents(StringBuilder builder, TelemetrySessionSummary summary)
    {
        builder.AppendLine("## 4. 关键变化 / Recording Events");
        builder.AppendLine();
        if (summary.TopEvents.Count == 0)
        {
            builder.AppendLine("- 未记录到关键变化。");
        }
        else
        {
            foreach (var @event in summary.TopEvents)
            {
                var transition = string.IsNullOrWhiteSpace(@event.From)
                    ? string.Empty
                    : $"（{Inline(@event.From)} → {Inline(@event.To)}）";
                builder.AppendLine(
                    $"- { @event.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC · {@event.Type}{transition}：{Inline(@event.Detail)}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendIncidents(
        StringBuilder builder,
        SessionIncidentEnvelope? incidents)
    {
        builder.AppendLine("## 5. Windows Incident Evidence");
        builder.AppendLine();
        if (incidents is null || incidents.Incidents.Count == 0)
        {
            builder.AppendLine("本次时间窗口内未记录到相关事件。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"- 查询状态：{incidents.QueryStatus}");
        builder.AppendLine();
        foreach (var incident in incidents.Incidents)
        {
            builder.AppendLine(
                $"- [{Inline(incident.EvidenceId)}] {incident.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC · {incident.Category} · {Inline(incident.ProviderName)} Event {incident.EventId}：{Inline(incident.Summary)}");
        }

        builder.AppendLine();
    }

    private static void AppendAnalysis(
        StringBuilder builder,
        SessionAnalysisEnvelope? analysis)
    {
        builder.AppendLine("## 6. AI Analysis");
        builder.AppendLine();
        if (analysis is null)
        {
            builder.AppendLine("尚未生成 AI 分析。");
            builder.AppendLine();
            return;
        }

        var result = analysis.Result;
        builder.AppendLine($"- Provider：{Inline(analysis.ProviderName ?? "未记录")}");
        builder.AppendLine($"- Model：{Inline(analysis.ModelName)}");
        builder.AppendLine($"- Summary：{Inline(result.Summary)}");
        builder.AppendLine($"- Overall assessment：{result.OverallAssessment}");
        builder.AppendLine($"- Confidence：{result.Confidence.ToString("P0", CultureInfo.InvariantCulture)}");
        builder.AppendLine();

        builder.AppendLine("### Findings");
        if (result.Findings.Count == 0)
        {
            builder.AppendLine("- 无。");
        }
        else
        {
            foreach (var finding in result.Findings)
            {
                builder.AppendLine($"- [{finding.Category}] {Inline(finding.Title)}：{Inline(finding.Assessment)}");
                builder.AppendLine($"  - {Inline(finding.Explanation)}");
                builder.AppendLine($"  - Evidence IDs：{(finding.EvidenceIds.Count == 0 ? "无" : string.Join(", ", finding.EvidenceIds.Select(Inline)))}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("### Recommendations");
        if (result.Recommendations.Count == 0)
        {
            builder.AppendLine("- 无。");
        }
        else
        {
            foreach (var recommendation in result.Recommendations)
            {
                builder.AppendLine($"- {Inline(recommendation.Text)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("### Uncertainties");
        if (result.Uncertainties.Count == 0)
        {
            builder.AppendLine("- 无。");
        }
        else
        {
            foreach (var uncertainty in result.Uncertainties)
            {
                builder.AppendLine($"- {Inline(uncertainty)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"### Spoken summary\n\n{Inline(result.SpokenSummary)}");
        builder.AppendLine();
    }

    private static void AppendDataNotes(StringBuilder builder, TelemetrySessionSummary summary)
    {
        builder.AppendLine("## 7. 数据说明");
        builder.AppendLine();
        builder.AppendLine("- 时间上的相关性不等于因果关系（correlation ≠ causation）。");
        builder.AppendLine("- Telemetry 数值保留 canonical metric 与实际来源状态；缺失/未知数据不会被制造。");
        builder.AppendLine("- 本报告只读取已持久化 session、incident 和 analysis 结果；导出不会重新采样、查事件日志或调用 AI。");
        builder.AppendLine($"- 软件版本：{Inline(ApplicationVersion())}");
        builder.AppendLine();
    }

    private static async Task WriteZipAtomicallyAsync(
        string destinationPath,
        SessionBundle bundle,
        string sessionPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("目标文件路径缺少有效目录。" );
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                AddEntry(archive, "README.md", BuildReadme(bundle.Session));
                AddEntry(archive, "report.md", BuildMarkdown(bundle));
                AddEntry(archive, "session.json", await File.ReadAllBytesAsync(sessionPath, cancellationToken));

                var sessionDirectory = Path.GetDirectoryName(sessionPath)!;
                AddOptionalFile(archive, "incidents.json", Path.Combine(sessionDirectory, "incidents.json"));
                AddOptionalFile(archive, "analysis.json", Path.Combine(sessionDirectory, "analysis.json"));
                // voice.wav is a presentation cache, intentionally excluded.
            }

            await using var flush = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            flush.Flush(flushToDisk: true);
            flush.Close();
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Preserve the primary export error.
            }
        }
    }

    private static void AddOptionalFile(ZipArchive archive, string entryName, string path)
    {
        if (File.Exists(path))
        {
            AddEntry(archive, entryName, File.ReadAllBytes(path));
        }
    }

    private static void AddEntry(ZipArchive archive, string entryName, string content) =>
        AddEntry(archive, entryName, Utf8WithoutBom.GetBytes(content));

    private static void AddEntry(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var target = entry.Open();
        target.Write(content, 0, content.Length);
    }

    private static string BuildReadme(TelemetryRecordingSession session) => $@"# AIGeekTuner Evidence Package

本证据包对应一个已完成的录制会话，不包含其它 Session、应用日志、设置或 Provider 凭据。

- Session ID：{Inline(session.Id)}
- 软件版本：{Inline(ApplicationVersion())}
- 导出时间（UTC）：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss zzz}
- Schema：session.json 当前录制 schema；incidents.json / analysis.json（如存在）为 schemaVersion 1
- session.json：原始 canonical telemetry session 与持久化统计
- incidents.json：可选的 Windows Incident evidence
- analysis.json：可选的 Session AI 分析结果
- report.md：面向用户的可读汇总

AI analysis 与原始 telemetry 是分离层；时间相关性不等于因果关系。缺失/未知值不会被制造。
";

    private static string Inline(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "未知"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Table(string? value) => Inline(value).Replace('|', '/');

    private static string ApplicationVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private sealed record SessionBundle(
        TelemetryRecordingSession Session,
        SessionIncidentEnvelope? Incidents,
        SessionAnalysisEnvelope? Analysis);
}
