using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>
    /// HWiNFO Provider：通过 seam 读取共享内存快照并映射 canonical。
    /// 状态语义（§19）：
    /// - 进程不在 + SHM 不可得 → Unavailable（未检测到正在运行的 HWiNFO）；
    /// - 进程在 + SHM 不可得 → NeedsConfiguration（提示启用 Shared Memory Support，
    ///   并如实说明免费版 12 小时时限；绝不自动绕过）；
    /// - 快照可得 → Ready；读取异常 → Error。
    /// </summary>
    public sealed class HwInfoTelemetryProvider : ITelemetryProvider
    {
        private readonly IHwInfoSensorReader _reader;
        private readonly IHwInfoProcessDetector _processDetector;

        public HwInfoTelemetryProvider(
            IHwInfoSensorReader reader,
            IHwInfoProcessDetector? processDetector = null)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _processDetector = processDetector ?? new HwInfoProcessDetector();
        }

        public TelemetrySourceKind SourceKind => TelemetrySourceKind.HwInfo;

        public async Task<TelemetryProviderResult> ReadSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capturedAtUtc = DateTimeOffset.UtcNow;

            var outcome = await _reader.ReadAsync(cancellationToken);
            if (outcome.Available)
            {
                var rawReadings = BuildRawReadings(outcome, capturedAtUtc, out var devices);
                var canonicalReadings = HwInfoCanonicalMapper.Map(
                    outcome.Sensors,
                    outcome.Readings,
                    capturedAtUtc);

                return new TelemetryProviderResult(
                    TelemetrySourceStatus.Ready,
                    $"HWiNFO 共享传感器读取成功（{rawReadings.Count} 项）。",
                    rawReadings,
                    canonicalReadings,
                    capturedAtUtc,
                    devices,
                    outcome.SourceVersion);
            }

            if (outcome.FailureDetail is not null)
            {
                return TelemetryProviderResult.Empty(
                    SourceKind,
                    TelemetrySourceStatus.Error,
                    "HWiNFO 共享内存读取失败。",
                    capturedAtUtc);
            }

            return _processDetector.IsRunning()
                ? TelemetryProviderResult.Empty(
                    SourceKind,
                    TelemetrySourceStatus.NeedsConfiguration,
                    "检测到 HWiNFO 正在运行，但未检测到共享传感器数据。"
                    + "请启动 HWiNFO Sensors，并在设置中启用 Shared Memory Support"
                    + "（免费版该功能有 12 小时时限，到期后需手动重新开启）。",
                    capturedAtUtc)
                : TelemetryProviderResult.Empty(
                    SourceKind,
                    TelemetrySourceStatus.Unavailable,
                    "未检测到正在运行的 HWiNFO。",
                    capturedAtUtc);
        }

        private static IReadOnlyList<RawTelemetryReading> BuildRawReadings(
            HwInfoReaderOutcome outcome,
            DateTimeOffset capturedAtUtc,
            out IReadOnlyList<SourceDeviceInfo> devices)
        {
            var deviceByIndex = new Dictionary<uint, SourceDeviceInfo>();
            var readings = new List<RawTelemetryReading>();
            foreach (var entry in outcome.Readings)
            {
                if (!deviceByIndex.TryGetValue(entry.SensorIndex, out var info))
                {
                    info = HwInfoCanonicalMapper.DescribeSourceDevice(
                        outcome.Sensors,
                        entry.SensorIndex);
                    deviceByIndex[entry.SensorIndex] = info;
                }

                var identity = new TelemetryDeviceIdentity(
                    info.Kind,
                    info.NativeDeviceId,
                    info.NativeDeviceName);

                readings.Add(new RawTelemetryReading(
                    TelemetrySourceKind.HwInfo,
                    $"{entry.SensorIndex}:{entry.ReadingId}",
                    entry.Label,
                    entry.Value,
                    HwInfoCanonicalMapper.NormalizeUnitText(entry.Unit),
                    identity,
                    info,
                    capturedAtUtc));
            }

            devices = deviceByIndex.Values.ToArray();
            return readings;
        }
    }
}
