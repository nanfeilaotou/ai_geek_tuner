using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Diagnostics;
using System.Diagnostics;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// 遥测聚合实现：
    /// 1) 并行读取全部 Provider，单源失败被完全隔离（一个 Provider 崩不拖垮整体）；
    /// 2) 每个 Provider 有独立超时，慢源不会让页面永远 Loading；
    /// 3) 对“同设备 + 同规范指标”按固定优先级 HWiNFO → AIDA64 → LHM 选择，
    ///    绝不对多个来源的数值做平均或混合；
    /// 4) 输出统一快照 + 各来源状态报告。
    /// </summary>
    public sealed class TelemetryHub : ITelemetryHub
    {
        /// <summary>第一版固定来源优先级（§12）；数值即选择顺序。</summary>
        public static readonly IReadOnlyList<TelemetrySourceKind> FixedPriorityOrder =
        [
            TelemetrySourceKind.HwInfo,
            TelemetrySourceKind.Aida64,
            TelemetrySourceKind.LibreHardwareMonitor
        ];

        private static readonly TimeSpan DefaultPerProviderTimeout = TimeSpan.FromSeconds(8);

        private readonly ITelemetryProvider[] _providers;
        private readonly TimeSpan _perProviderTimeout;

        public TelemetryHub(
            IEnumerable<ITelemetryProvider> providers,
            TimeSpan? perProviderTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(providers);

            var materialized = providers.ToArray();
            var duplicates = materialized
                .GroupBy(provider => provider.SourceKind)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicates is not null)
            {
                throw new ArgumentException(
                    $"Duplicate provider for source '{duplicates.Key}'.",
                    nameof(providers));
            }

            _providers = FixedPriorityOrder
                .Select(kind => materialized
                    .FirstOrDefault(provider => provider.SourceKind == kind))
                .Where(provider => provider is not null)
                .Cast<ITelemetryProvider>()
                .ToArray();
            _perProviderTimeout = perProviderTimeout ?? DefaultPerProviderTimeout;
        }

        public async Task<TelemetrySnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tasks = _providers
                .Select(provider => ReadIsolatedAsync(provider, cancellationToken))
                .ToArray();
            // ReadIsolatedAsync 只在“外层取消”时抛出，业务失败一律降级为 Error 报告。
            await Task.WhenAll(tasks);

            var outcomes = new Dictionary<TelemetrySourceKind, ProviderOutcome>();
            for (var i = 0; i < _providers.Length; i++)
            {
                outcomes[_providers[i].SourceKind] = await tasks[i];
            }

            // 设备 Reconciliation（§14）：跨源设备合并只依据证据；
            // 未合并的源本地设备保留独立命名空间，绝不因 ordinal 相同而互通。
            var reconciliation = TelemetryDeviceReconciler.ToLookup(
                ReconcileDevices(outcomes));

            var canonicalReadings =
                SelectCanonicalReadings(outcomes, reconciliation);
            var sourceReports = BuildSourceReports(outcomes);

            var rawReadings = outcomes.Values
                .Where(outcome => outcome.HasUsableData)
                .Select(outcome => outcome.Result!)
                .SelectMany(result => result.RawReadings)
                .ToArray();

            return new TelemetrySnapshot(
                DateTimeOffset.UtcNow,
                canonicalReadings,
                sourceReports,
                rawReadings);
        }

        private static IReadOnlyList<CanonicalDeviceGroup> ReconcileDevices(
            IReadOnlyDictionary<TelemetrySourceKind, ProviderOutcome> outcomes)
        {
            var devices = outcomes.Values
                .Where(outcome => outcome.HasUsableData)
                .Select(outcome => outcome.Result!)
                .ToArray();
            var deviceInfos = devices.SelectMany(result => result.Devices).ToArray();
            return TelemetryDeviceReconciler.Reconcile(deviceInfos);
        }

        private static List<TelemetryReading> SelectCanonicalReadings(
            IReadOnlyDictionary<TelemetrySourceKind, ProviderOutcome> outcomes,
            DeviceReconciliationLookup lookup)
        {
            var selected = new List<TelemetryReading>();
            var claimedKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kind in FixedPriorityOrder)
            {
                if (!outcomes.TryGetValue(kind, out var outcome))
                {
                    continue;
                }

                var sourceResult = outcome.Result;
                if (sourceResult is null || !outcome.HasUsableData)
                {
                    continue;
                }

                foreach (var reading in sourceResult.CanonicalReadings)
                {
                    // 把源侧本地设备映射到 canonical 身份；未合并的设备落在
                    // src:{source}:{nativeId} 命名空间中，天然与其他来源隔离。
                    if (!lookup.TryResolve(kind, reading.Device.DeviceKey, out var canonical))
                    {
                        canonical = reading.Device;
                    }

                    // 选择键 = canonical 设备 + 指标（绝不含来源）：
                    // 同一物理设备的同指标只允许最高优先级来源占位。
                    var claimKey = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{(int)canonical.Kind}{canonical.DeviceKey}{reading.MetricKey.Value}");
                    if (claimedKeys.Add(claimKey))
                    {
                        selected.Add(new TelemetryReading(
                            reading.MetricKey,
                            reading.Value,
                            reading.Unit,
                            canonical,
                            reading.Source,
                            reading.SourceMetricId,
                            reading.SourceLabel,
                            reading.CapturedAtUtc));
                    }
                }
            }

            return selected;
        }

        private static IReadOnlyList<TelemetrySourceReport> BuildSourceReports(
            IReadOnlyDictionary<TelemetrySourceKind, ProviderOutcome> outcomes)
        {
            var reports = new List<TelemetrySourceReport>();
            foreach (var kind in FixedPriorityOrder)
            {
                if (!outcomes.TryGetValue(kind, out var outcome))
                {
                    continue;
                }

                DateTimeOffset? lastReadAtUtc = null;
                int canonicalCount = 0;
                string? sourceVersion = null;
                var result = outcome.Result;
                if (result is not null)
                {
                    sourceVersion = result.SourceVersion;
                    if (result.Status is TelemetrySourceStatus.Ready
                        or TelemetrySourceStatus.Degraded)
                    {
                        lastReadAtUtc = result.CapturedAtUtc;
                        canonicalCount = result.CanonicalReadings.Count;
                    }
                }

                reports.Add(new TelemetrySourceReport(
                    kind,
                    outcome.Status,
                    outcome.Message,
                    outcome.RawReadingCount,
                    lastReadAtUtc,
                    canonicalCount,
                    outcome.ReadDurationMs,
                    sourceVersion));
            }

            return reports;
        }

        private async Task<ProviderOutcome> ReadIsolatedAsync(
            ITelemetryProvider provider,
            CancellationToken outerToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            linkedCts.CancelAfter(_perProviderTimeout);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await provider.ReadSnapshotAsync(linkedCts.Token);
                stopwatch.Stop();
                return ProviderOutcome.From(result, stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
                when (outerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                stopwatch.Stop();
                Log(provider, exception, "timed out");
                return ProviderOutcome.TimeoutFailure(
                    $"读取超时（>{_perProviderTimeout.TotalSeconds:0.#} 秒）。",
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Log(provider, exception, "failed");
                return ProviderOutcome.RuntimeFailure("读取失败。", stopwatch.ElapsedMilliseconds);
            }
        }

        private static void Log(
            ITelemetryProvider provider,
            Exception exception,
            string verb) =>
            ExceptionLogWriter.Write(
                exception,
                $"Telemetry/{provider.SourceKind} {verb}");

        private sealed record ProviderOutcome(
            TelemetrySourceStatus Status,
            string Message,
            int RawReadingCount,
            TelemetryProviderResult? Result,
            long ReadDurationMs)
        {
            public bool HasUsableData =>
                Result is not null
                && Status is TelemetrySourceStatus.Ready or TelemetrySourceStatus.Degraded;

            public static ProviderOutcome From(TelemetryProviderResult result, long durationMs) =>
                new(result.Status, result.Message, result.RawReadings.Count, result, durationMs);

            public static ProviderOutcome TimeoutFailure(string message, long durationMs) =>
                new(TelemetrySourceStatus.Error, message, 0, null, durationMs);

            public static ProviderOutcome RuntimeFailure(string message, long durationMs) =>
                new(TelemetrySourceStatus.Error, message, 0, null, durationMs);
        }
    }
}
