using System.Globalization;
using System.Text.RegularExpressions;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>
    /// 通过 root\WMI\AIDA64_SensorValues 读取 AIDA64 External Applications 导出。
    /// 状态语义（§17）：类不存在 + 进程不在 → Unavailable；
    /// 类不存在 + 检测到 aida64.exe → NeedsConfiguration（给用户启用指引）；
    /// 成功 → Ready。绝不自动修改 AIDA64 配置。
    /// </summary>
    public sealed class Aida64TelemetryProvider : ITelemetryProvider
    {
        private static readonly Regex GpuMemClockRegex =
            new("^SGPU\\d{1,2}MEMCLK$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IAida64WmiReader _reader;
        private readonly IAida64ProcessDetector _processDetector;

        public Aida64TelemetryProvider(
            IAida64WmiReader reader,
            IAida64ProcessDetector? processDetector = null)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _processDetector = processDetector ?? new Aida64ProcessDetector();
        }

        public TelemetrySourceKind SourceKind => TelemetrySourceKind.Aida64;

        public async Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => ReadCore(cancellationToken), cancellationToken);
        }

        private TelemetryProviderResult ReadCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capturedAtUtc = DateTimeOffset.UtcNow;

            var query = _reader.Query();
            if (!query.Succeeded)
            {
                return BuildFailureResult(query, capturedAtUtc);
            }

            var rawReadings = new List<RawTelemetryReading>();
            var devices = new Dictionary<(TelemetryDeviceKind Kind, string NativeId), SourceDeviceInfo>();
            var skippedMalformed = 0;
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = 0;

            foreach (var row in query.Rows)
            {
                if (row is null
                    || string.IsNullOrWhiteSpace(row.Id)
                    || !TelemetryUnitConversion.TryParseInvariant(row.Value, out var value))
                {
                    skippedMalformed++;
                    continue;
                }

                var id = row.Id.Trim();
                if (!seenIds.Add(id))
                {
                    duplicates++;
                }

                if (!Aida64CanonicalMapper.TryClassifyDeviceAndUnit(
                        id,
                        out var device,
                        out var unit))
                {
                    // 完全未收录的 ID：保留为 Raw（挂 system 设备），绝不猜测映射。
                    device = TelemetryDeviceIdentity.SystemBoard("system");
                    unit = TelemetryUnit.None;
                }

                var info = DescribeDevice(device);
                devices[(info.Kind, info.NativeDeviceId)] = info;

                rawReadings.Add(new RawTelemetryReading(
                    TelemetrySourceKind.Aida64,
                    id,
                    row.Label ?? id,
                    value,
                    unit,
                    device,
                    info,
                    capturedAtUtc));
            }

            var canonicalReadings = Aida64CanonicalMapper.Map(rawReadings);

            return new TelemetryProviderResult(
                TelemetrySourceStatus.Ready,
                $"AIDA64 传感器读取成功（{rawReadings.Count} 项"
                + (skippedMalformed > 0 ? $"，跳过异常 {skippedMalformed} 项" : "")
                + (duplicates > 0 ? $"，重复 {duplicates} 项" : "")
                + "）。",
                rawReadings,
                canonicalReadings,
                capturedAtUtc,
                devices.Values.ToArray());
        }

        /// <summary>由源侧设备身份推导设备事实（AIDA WMI 不提供型号名，名称保持派生标签）。</summary>
        private static SourceDeviceInfo DescribeDevice(TelemetryDeviceIdentity device)
        {
            var nativeName = device.DisplayName;
            return device.DeviceKey switch
            {
                "cpu" => new SourceDeviceInfo(
                    TelemetrySourceKind.Aida64, TelemetryDeviceKind.Cpu, "cpu", nativeName, 0, []),
                "memory" => new SourceDeviceInfo(
                    TelemetrySourceKind.Aida64, TelemetryDeviceKind.Memory, "memory", nativeName, 0, []),
                _ when device.DeviceKey.StartsWith("gpu:", StringComparison.Ordinal)
                    && int.TryParse(device.DeviceKey.AsSpan("gpu:".Length), out var gpuOrdinal) =>
                    new SourceDeviceInfo(
                        TelemetrySourceKind.Aida64,
                        TelemetryDeviceKind.Gpu,
                        device.DeviceKey,
                        nativeName,
                        gpuOrdinal,
                        []),
                _ when device.DeviceKey.StartsWith("storage:hdd:", StringComparison.Ordinal)
                    && int.TryParse(device.DeviceKey.AsSpan("storage:hdd:".Length), out var hddOrdinal) =>
                    new SourceDeviceInfo(
                        TelemetrySourceKind.Aida64,
                        TelemetryDeviceKind.Storage,
                        device.DeviceKey,
                        nativeName,
                        hddOrdinal - 1,
                        []),
                _ when device.DeviceKey.StartsWith("memory-module:", StringComparison.Ordinal)
                    && int.TryParse(device.DeviceKey.AsSpan("memory-module:".Length), out var moduleOrdinal) =>
                    // V2-M4.5B Gate E：source-local 模块事实，供 Reconciler 评估。
                    new SourceDeviceInfo(
                        TelemetrySourceKind.Aida64,
                        TelemetryDeviceKind.MemoryModule,
                        device.DeviceKey,
                        nativeName,
                        moduleOrdinal,
                        []),
                _ => new SourceDeviceInfo(
                    TelemetrySourceKind.Aida64,
                    TelemetryDeviceKind.System,
                    device.DeviceKey,
                    nativeName,
                    0,
                    [])
            };
        }

        private TelemetryProviderResult BuildFailureResult(
            Aida64WmiQueryResult query,
            DateTimeOffset capturedAtUtc) =>
            query.FailureKind switch
            {
                Aida64WmiFailureKind.ClassMissing when _processDetector.IsRunning() =>
                    TelemetryProviderResult.Empty(
                        SourceKind,
                        TelemetrySourceStatus.NeedsConfiguration,
                        "检测到 AIDA64 正在运行，但尚未启用 WMI 导出。"
                        + "请在 AIDA64：File → Preferences → Hardware Monitoring → "
                        + "External Applications 勾选 WMI Sensor Values 后重试。",
                        capturedAtUtc),

                Aida64WmiFailureKind.ClassMissing =>
                    TelemetryProviderResult.Empty(
                        SourceKind,
                        TelemetrySourceStatus.Unavailable,
                        "未检测到 AIDA64 传感器数据。",
                        capturedAtUtc),

                _ =>
                    TelemetryProviderResult.Empty(
                        SourceKind,
                        TelemetrySourceStatus.Error,
                        "AIDA64 WMI 查询失败。",
                        capturedAtUtc)
            };
    }
}
