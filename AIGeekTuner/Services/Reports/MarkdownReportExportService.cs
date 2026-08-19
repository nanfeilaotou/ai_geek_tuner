using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Reports
{
    public sealed class MarkdownReportExportService : IReportExportService
    {
        private static readonly Encoding Utf8WithoutBom =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly string _reportsDirectory;

        public MarkdownReportExportService(string? reportsDirectory = null)
        {
            _reportsDirectory = Path.GetFullPath(
                reportsDirectory
                ?? ApplicationDataPaths.Default.ReportsDirectory);
        }

        public MarkdownReportExportService(ApplicationDataPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _reportsDirectory = paths.ReportsDirectory;
        }

        public async Task<string> ExportAsync(
            DiagnosisOutcome outcome,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(outcome);

            try
            {
                Directory.CreateDirectory(_reportsDirectory);
                var markdown = BuildMarkdown(outcome);
                var baseFileName =
                    $"AI-GeekTuner_Report_{outcome.CompletedAt.ToLocalTime():yyyyMMdd_HHmmss}";

                for (var suffix = 0; suffix < 1000; suffix++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileName = suffix == 0
                        ? $"{baseFileName}.md"
                        : $"{baseFileName}_{suffix}.md";
                    var destinationPath = Path.Combine(
                        _reportsDirectory,
                        fileName);

                    try
                    {
                        await using var stream = new FileStream(
                            destinationPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 16 * 1024,
                            useAsync: true);
                        await using var writer = new StreamWriter(
                            stream,
                            Utf8WithoutBom);
                        await writer.WriteAsync(
                            markdown.AsMemory(),
                            cancellationToken);
                        return destinationPath;
                    }
                    catch (IOException) when (File.Exists(destinationPath))
                    {
                        // A report with the same timestamp already exists.
                    }
                }

                var fallbackPath = Path.Combine(
                    _reportsDirectory,
                    $"{baseFileName}_{Guid.NewGuid():N}.md");
                await File.WriteAllTextAsync(
                    fallbackPath,
                    markdown,
                    Utf8WithoutBom,
                    cancellationToken);
                return fallbackPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or SecurityException
                    or NotSupportedException
                    or ArgumentException)
            {
                throw new ReportExportException(
                    "诊断报告写入本地数据目录失败，请检查目录访问权限。",
                    exception);
            }
        }

        private static string BuildMarkdown(DiagnosisOutcome outcome)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# AI-GeekTuner 诊断报告");
            builder.AppendLine();
            AppendMetadata(builder, outcome);
            AppendHardware(builder, outcome.Request.Hardware);
            AppendDiagnosis(builder, outcome.AiResult);
            AppendEvidence(builder, outcome.AiResult.Evidence);
            AppendRecommendations(builder, outcome);
            AppendSafety(builder, outcome.Safety);
            return builder.ToString();
        }

        private static void AppendMetadata(StringBuilder builder, DiagnosisOutcome outcome)
        {
            builder.AppendLine("## 基本信息");
            builder.AppendLine();
            builder.AppendLine($"- 诊断时间：{outcome.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"- 原始日志名称：{GetLogDisplayName(outcome.Request.FaultLog.FileName)}");
            builder.AppendLine();
        }

        private static void AppendHardware(StringBuilder builder, HardwareInfo hardware)
        {
            builder.AppendLine("## 本机硬件摘要");
            builder.AppendLine();
            builder.AppendLine($"- CPU：{NormalizeInline(hardware.CpuName)}");
            builder.AppendLine($"- GPU：{JoinValues(hardware.GpuNames)}");
            builder.AppendLine($"- 内存容量：{FormatBytes(hardware.TotalMemoryBytes)}");
            builder.AppendLine($"- 内存厂商：{JoinValues(hardware.MemoryManufacturers)}");
            builder.AppendLine($"- 内存速度：{FormatMemorySpeeds(hardware.MemorySpeedsMHz)}");
            builder.AppendLine($"- 主板：{NormalizeInline(hardware.MotherboardManufacturer)} {NormalizeInline(hardware.MotherboardProduct)}".TrimEnd());
            builder.AppendLine($"- 操作系统：{NormalizeInline(hardware.OperatingSystemName)} {NormalizeInline(hardware.OperatingSystemVersion)} {NormalizeInline(hardware.OperatingSystemArchitecture)}".TrimEnd());
            builder.AppendLine();
        }

        private static void AppendDiagnosis(StringBuilder builder, DiagnosticResult result)
        {
            builder.AppendLine("## 诊断结论");
            builder.AppendLine();
            builder.AppendLine("### 诊断摘要");
            builder.AppendLine();
            builder.AppendLine(result.Summary.Trim());
            builder.AppendLine();
            builder.AppendLine("### 根本原因");
            builder.AppendLine();
            builder.AppendLine(result.RootCause.Trim());
            builder.AppendLine();
            builder.AppendLine($"- AI 置信度：{Math.Clamp(result.Confidence, 0, 1).ToString("P0", CultureInfo.GetCultureInfo("zh-CN"))}");
            builder.AppendLine($"- 风险等级：{result.RiskLevel}");
            builder.AppendLine();
        }

        private static void AppendEvidence(
            StringBuilder builder,
            IReadOnlyList<DiagnosticEvidence> evidence)
        {
            builder.AppendLine("## 证据链");
            builder.AppendLine();
            builder.AppendLine("### 事实证据");
            builder.AppendLine();
            AppendList(builder, evidence
                .Where(item => item.Kind == EvidenceKind.Fact)
                .Select(item => item.Description));
            builder.AppendLine();
            builder.AppendLine("### AI 推测");
            builder.AppendLine();
            AppendList(builder, evidence
                .Where(item => item.Kind == EvidenceKind.Inference)
                .Select(item => item.Description));
            builder.AppendLine();
        }

        private static void AppendRecommendations(StringBuilder builder, DiagnosisOutcome outcome)
        {
            builder.AppendLine("## 建议操作");
            builder.AppendLine();

            if (outcome.Safety.Status == SafetyStatus.Rejected)
            {
                builder.AppendLine("本次AI输出已被SafetyGuard拦截。");
                builder.AppendLine();
                return;
            }

            if (outcome.AiResult.Recommendations.Count == 0)
            {
                builder.AppendLine("暂无建议操作。");
                builder.AppendLine();
                return;
            }

            for (var index = 0; index < outcome.AiResult.Recommendations.Count; index++)
            {
                var recommendation = outcome.AiResult.Recommendations[index];
                builder.AppendLine($"### {index + 1}. {NormalizeInline(recommendation.Action)}");
                builder.AppendLine();
                builder.AppendLine($"- 原因：{NormalizeInline(recommendation.Reason)}");
                builder.AppendLine($"- 风险等级：{recommendation.RiskLevel}");
                builder.AppendLine("- 注意事项：");
                if (recommendation.Precautions.Count == 0)
                {
                    builder.AppendLine("  - 无");
                }
                else
                {
                    foreach (var precaution in recommendation.Precautions)
                    {
                        builder.AppendLine($"  - {NormalizeInline(precaution)}");
                    }
                }

                builder.AppendLine();
            }
        }

        private static void AppendSafety(StringBuilder builder, SafetyResult safety)
        {
            builder.AppendLine("## SafetyGuard 状态");
            builder.AppendLine();
            builder.AppendLine($"- 状态：{safety.Status}");

            if (safety.Status == SafetyStatus.Rejected)
            {
                builder.AppendLine("- 本次AI输出已被SafetyGuard拦截。");
                builder.AppendLine();
                return;
            }

            if (safety.Warnings.Count > 0)
            {
                builder.AppendLine("- 警告：");
                foreach (var warning in safety.Warnings)
                {
                    builder.AppendLine($"  - {NormalizeInline(warning)}");
                }
            }

            builder.AppendLine();
        }

        private static void AppendList(StringBuilder builder, IEnumerable<string> values)
        {
            var items = values.ToArray();
            if (items.Length == 0)
            {
                builder.AppendLine("- 无");
                return;
            }

            foreach (var item in items)
            {
                builder.AppendLine($"- {NormalizeInline(item)}");
            }
        }

        private static string GetLogDisplayName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "粘贴日志";
            }

            try
            {
                return NormalizeInline(Path.GetFileName(fileName));
            }
            catch (ArgumentException)
            {
                return NormalizeInline(fileName);
            }
        }

        private static string JoinValues(IReadOnlyList<string> values) =>
            values.Count == 0
                ? HardwareInfo.UnknownValue
                : string.Join(" / ", values.Select(NormalizeInline));

        private static string FormatBytes(ulong? bytes) =>
            bytes.HasValue
                ? $"{bytes.Value / 1024d / 1024d / 1024d:0.##} GB"
                : HardwareInfo.UnknownValue;

        private static string FormatMemorySpeeds(IReadOnlyList<uint> speeds) =>
            speeds.Count == 0
                ? HardwareInfo.UnknownValue
                : string.Join(" / ", speeds.Select(speed => $"{speed} MHz"));

        private static string NormalizeInline(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? HardwareInfo.UnknownValue
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
