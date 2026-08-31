using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>Hardware 页单卡内一行核心指标。</summary>
    public sealed record HardwarePageMetricRow(
        string Label,
        string ValueText,
        string SourceDisplay);

    /// <summary>Hardware 页一张设备卡：已按显示策略与核心指标闭集筛选。</summary>
    public sealed record HardwarePageDeviceCard(
        string Title,
        TelemetryDeviceKind Kind,
        string DeviceKey,
        string FullName,
        IReadOnlyList<HardwarePageMetricRow> Rows);

    /// <summary>Hardware 页默认“核心趋势”一行（§7/§8：策略驱动，非全量自动生成）。</summary>
    public sealed record HardwarePageTrendRow(
        string Label,
        string FullName,
        string Current,
        string Min,
        string Max,
        IReadOnlyList<double> Points);

    /// <summary>
    /// V2-M3.3 普通用户 Hardware 页视图装配器（纯展示层，只读 snapshot）。
    ///
    /// - §6 核心指标闭集：CPU=温度/使用率/时钟/Package Power；
    ///   GPU=温度/使用率/核心频率/功耗（Hotspot、显存温度、已用显存属 Raw/更多趋势）；
    ///   内存=已用/使用率/频率；磁盘=主温度。存在才显示。
    /// - §7 默认核心趋势：CPU 三条 + 独显四条 + 内存使用率 ≈ 6–8 行；
    ///   Intel iGPU、存储温度、显存相关默认不进趋势区（§8/§9）。
    /// - §5 显示名 resolver：匿名存储设备仅在“同快照无任何真实盘名”时
    ///   以“磁盘 #N”回退显示；绝不写 HDD（实际可能是 NVMe/SSD）。
    ///   GPU 匿名设备无回退——宁可少显示，不可错误归属（§2/§3）。
    /// 绝不修改 canonical identity，绝不消费 RawReadings。
    /// </summary>
    public static class HardwarePageViewBuilder
    {
        // ---- §6 核心指标闭集（存在才显示；Metric Catalog 不扩大）----
        private static readonly IReadOnlyList<string> CpuCoreMetrics =
        [
            "cpu.package.temperature", "cpu.total.utilization", "cpu.clock", "cpu.package.power"
        ];

        private static readonly IReadOnlyList<string> GpuCoreMetrics =
        [
            "gpu.core.temperature", "gpu.core.utilization", "gpu.core.clock", "gpu.board.power"
        ];

        private static readonly IReadOnlyList<string> MemoryCoreMetrics =
        [
            "memory.used", "memory.utilization", "memory.clock"
        ];

        private static readonly IReadOnlyList<string> StorageCoreMetrics = ["storage.temperature"];

        // ---- §7 默认核心趋势闭集 ----
        private static readonly IReadOnlyList<string> CpuTrendMetrics =
        [
            "cpu.package.temperature", "cpu.total.utilization", "cpu.package.power"
        ];

        private static readonly IReadOnlyList<string> GpuTrendMetrics =
        [
            "gpu.core.temperature", "gpu.core.utilization", "gpu.core.clock", "gpu.board.power"
        ];

        private static readonly IReadOnlyList<string> MemoryTrendMetrics = ["memory.utilization"];

        public static bool IsCoreMetric(TelemetryDeviceKind kind, string metricKey) =>
            AllowedCoreMetrics(kind).Contains(metricKey, StringComparer.Ordinal);

        private static IReadOnlyList<string> AllowedCoreMetrics(TelemetryDeviceKind kind) =>
            kind switch
            {
                TelemetryDeviceKind.Cpu => CpuCoreMetrics,
                TelemetryDeviceKind.Gpu => GpuCoreMetrics,
                TelemetryDeviceKind.Memory => MemoryCoreMetrics,
                TelemetryDeviceKind.Storage => StorageCoreMetrics,
                _ => [],
            };

        private static IReadOnlyList<string> AllowedTrendMetrics(TelemetryDeviceKind kind) =>
            kind switch
            {
                TelemetryDeviceKind.Cpu => CpuTrendMetrics,
                TelemetryDeviceKind.Gpu => GpuTrendMetrics,
                TelemetryDeviceKind.Memory => MemoryTrendMetrics,
                _ => [],
            };

        /// <summary>§9：Intel iGPU 属“更多趋势”，默认核心趋势不显示。</summary>
        public static bool IsIntelIntegratedGpu(string displayName) =>
            displayName.Contains("intel", StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<HardwarePageDeviceCard> BuildCards(
            TelemetrySnapshot snapshot,
            Func<TelemetryMetricKey, string?> metricLabel,
            Func<double, TelemetryUnit, string> formatValue,
            Func<TelemetrySourceKind, string> sourceDisplay)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var deviceGroups = snapshot.CanonicalReadings
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .Select(group => group.ToArray())
                .OrderBy(group => OrderOf(group[0].Device.Kind))
                .ThenBy(group => group[0].Device.Kind == TelemetryDeviceKind.Gpu
                    && IsIntelIntegratedGpu(group[0].Device.DisplayName) ? 1 : 0)
                .ThenBy(group => group[0].Device.DeviceKey, StringComparer.Ordinal)
                .ToArray();

            // §5：存储显示名 resolver——真实盘名优先；
            // 只有整张快照没有任何真实盘名时，匿名存储才以“磁盘 #N”回退。
            var hasNamedStorage = deviceGroups.Any(group =>
                group[0].Device.Kind == TelemetryDeviceKind.Storage
                && TelemetryDeviceReconciler.NormalizeName(group[0].Device.DisplayName).Length > 0);
            var anonymousStorageOrdinal = 0;

            var cards = new List<HardwarePageDeviceCard>();
            foreach (var group in deviceGroups)
            {
                var device = group[0].Device;
                if (!HardwareDisplayPolicy.IsUserVisible(device))
                {
                    continue; // 匿名 GPU 等不进主界面（§3），数据仍在 Raw 明细。
                }

                if (device.Kind == TelemetryDeviceKind.Storage
                    && TelemetryDeviceReconciler.NormalizeName(device.DisplayName).Length == 0)
                {
                    if (hasNamedStorage)
                    {
                        // 已有真实盘名卡片展示，匿名来源大概率是其重复视图；
                        // 无归属证据时宁可少显示（§2），不猜测对应关系。
                        continue;
                    }

                    anonymousStorageOrdinal++;
                }

                var rows = group
                    .Where(reading => IsCoreMetric(device.Kind, reading.MetricKey.Value)
                        && metricLabel(reading.MetricKey) is not null)
                    .GroupBy(reading => reading.MetricKey.Value, StringComparer.Ordinal)
                    .Select(metricGroup => metricGroup.First())
                    .OrderBy(reading => CoreMetricOrder(device.Kind, reading.MetricKey.Value))
                    .Select(reading => new HardwarePageMetricRow(
                        metricLabel(reading.MetricKey)!,
                        formatValue(reading.Value, reading.Unit),
                        sourceDisplay(reading.Source)))
                    .ToArray();
                if (rows.Length == 0)
                {
                    continue;
                }

                cards.Add(new HardwarePageDeviceCard(
                    CardTitle(device, hasNamedStorage, anonymousStorageOrdinal),
                    device.Kind,
                    device.DeviceKey,
                    device.DisplayName,
                    rows));
            }

            return cards;
        }

        public static IReadOnlyList<HardwarePageTrendRow> BuildTrends(
            TelemetrySnapshot snapshot,
            TelemetryTrendBuffer trendBuffer,
            Func<TelemetryMetricKey, string?> metricLabel,
            Func<double, TelemetryUnit, string> formatValue)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(trendBuffer);

            var rows = snapshot.CanonicalReadings
                .Where(reading => HardwareDisplayPolicy.IsUserVisible(reading.Device))
                // §9：iGPU 属“更多趋势”；§8：存储温度不进默认趋势。
                .Where(reading => AllowedTrendMetrics(reading.Device.Kind)
                    .Contains(reading.MetricKey.Value, StringComparer.Ordinal))
                .Where(reading => reading.Device.Kind != TelemetryDeviceKind.Gpu
                    || !IsIntelIntegratedGpu(reading.Device.DisplayName))
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .OrderBy(group => OrderOf(group.First().Device.Kind))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .SelectMany(group => group
                    .GroupBy(reading => reading.MetricKey.Value, StringComparer.Ordinal)
                    .Select(metricGroup => metricGroup.First())
                    .OrderBy(reading => AllowedTrendMetrics(group.First().Device.Kind)
                        .ToList()
                        .IndexOf(reading.MetricKey.Value)))
                .Select(reading => (Reading: reading, Points: trendBuffer.GetSeries(
                    reading.Device.DeviceKey,
                    reading.MetricKey.Value)))
                .Where(entry => entry.Points.Count > 0)
                .Select(entry =>
                {
                    var reading = entry.Reading;
                    // Sparkline 只画最近 120 点：2 分钟窗口在高频采样下足够平滑。
                    var values = entry.Points.Select(point => point.Value).ToArray();
                    if (values.Length > 120)
                    {
                        values = values.Skip(values.Length - 120).ToArray();
                    }

                    return new HardwarePageTrendRow(
                        $"{ShortTrendName(reading.Device)} · {metricLabel(reading.MetricKey)}",
                        reading.Device.DisplayName,
                        formatValue(values[^1], reading.Unit),
                        formatValue(values.Min(), reading.Unit),
                        formatValue(values.Max(), reading.Unit),
                        values);
                })
                .ToArray();
            return rows;
        }

        /// <summary>卡片标题（§5/§10）：单一实体用稳定中文名；存储回退名不带 HDD。</summary>
        public static string CardTitle(
            TelemetryDeviceIdentity device, bool hasNamedStorage, int anonymousStorageOrdinal)
        {
            if (device.Kind == TelemetryDeviceKind.Memory)
            {
                return "内存";
            }

            if (device.Kind == TelemetryDeviceKind.Storage
                && TelemetryDeviceReconciler.NormalizeName(device.DisplayName).Length == 0
                && !hasNamedStorage)
            {
                return $"磁盘 #{anonymousStorageOrdinal}";
            }

            return device.Kind switch
            {
                TelemetryDeviceKind.Cpu => $"CPU · {device.DisplayName}",
                TelemetryDeviceKind.Gpu => $"GPU · {device.DisplayName}",
                TelemetryDeviceKind.Storage => $"磁盘 · {device.DisplayName}",
                _ => device.DisplayName,
            };
        }

        /// <summary>§10 趋势短名：一行趋势不占半行设备全名（全名走 Tooltip）。</summary>
        public static string ShortTrendName(TelemetryDeviceIdentity device)
        {
            if (device.Kind == TelemetryDeviceKind.Cpu)
            {
                return "CPU";
            }

            if (device.Kind == TelemetryDeviceKind.Memory)
            {
                return "内存";
            }

            if (device.Kind == TelemetryDeviceKind.Gpu)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    device.DisplayName,
                    @"\b((?:RTX|GTX|MX)\s*\d+\w*)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    // “RTX 4080 Laptop GPU” → “RTX 4080”；空白折叠保持可读。
                    return System.Text.RegularExpressions.Regex.Replace(
                        match.Groups[1].Value, @"\s+", " ");
                }

                return System.Text.RegularExpressions.Regex.Replace(
                    device.DisplayName,
                    @"^(NVIDIA\s+GeForce\s+|Intel\(R\)\s+|Intel®\s+)",
                    string.Empty,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return device.DisplayName;
        }

        private static int CoreMetricOrder(TelemetryDeviceKind kind, string metricKey)
        {
            var list = AllowedCoreMetrics(kind);
            var index = list.ToList().IndexOf(metricKey);
            return index < 0 ? list.Count : index;
        }

        private static int OrderOf(TelemetryDeviceKind kind) => kind switch
        {
            TelemetryDeviceKind.Cpu => 0,
            TelemetryDeviceKind.Gpu => 1,
            TelemetryDeviceKind.Memory => 2,
            TelemetryDeviceKind.Storage => 3,
            _ => 4,
        };
    }
}
