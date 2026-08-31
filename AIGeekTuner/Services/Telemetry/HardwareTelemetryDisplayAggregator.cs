using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>Hardware 页展示用设备视图：一个物理设备一张卡片（§4）。</summary>
    public sealed record HardwareTelemetryDeviceView(
        string CanonicalDeviceKey,
        TelemetryDeviceKind Kind,
        string DisplayName,
        bool IsResolvedCluster,
        IReadOnlyList<DisplayMetricRow> Metrics);

    public sealed record DisplayMetricRow(
        string MetricLabel,
        string ValueText,
        string SourceDisplay,
        string EvidenceId);

    /// <summary>
    /// 显示聚合（§4/§5）：以 canonical 设备为卡片单位；重复指标只保留 Hub 选定读数。
    /// 未跨源合并的源本地设备（src:*）：若同 Kind 已有合并组卡片则并入该卡片并标注来源；
    /// 否则独立成卡。数据层不做任何猜测性 merge（§16）。
    /// </summary>
    public static class HardwareTelemetryDisplayAggregator
    {
        public static IReadOnlyList<HardwareTelemetryDeviceView> Build(
            TelemetrySnapshot snapshot,
            Func<TelemetryMetricKey, string> metricLabel,
            Func<double, TelemetryUnit, string> formatValue,
            Func<TelemetrySourceKind, string> sourceDisplay)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var resolvedCards = new List<HardwareTelemetryDeviceView>();
            var unresolvedByKind = new Dictionary<TelemetryDeviceKind,
                List<IGrouping<string, TelemetryReading>>>();

            foreach (var group in snapshot.CanonicalReadings
                .GroupBy(r => r.Device.DeviceKey)
                .OrderBy(g => OrderOf(g.First().Device.Kind))
                .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                if (!first.Device.DeviceKey.StartsWith("src:", StringComparison.Ordinal))
                {
                    resolvedCards.Add(new HardwareTelemetryDeviceView(
                        first.Device.DeviceKey,
                        first.Device.Kind,
                        first.Device.DisplayName,
                        true,
                        BuildRows(group, metricLabel, formatValue, sourceDisplay)));
                }
                else
                {
                    if (!unresolvedByKind.TryGetValue(first.Device.Kind, out var list))
                    {
                        unresolvedByKind[first.Device.Kind] = list = [];
                    }

                    list.Add(group);
                }
            }

            var result = new List<HardwareTelemetryDeviceView>(resolvedCards);
            // §Gate B 修正：unresolved 设备（含 AIDA 匿名 GPU）一律独立成卡，
            // 不并入任何 resolved 集群。宁可少显示，不可错误归属。
            // V2-M3.3 产品化：匿名占位设备（GPU #n / HDD #n）连独立卡也不进
            // 展示结果——数据仍完整存在于 Raw 明细，绝不因减少重复而猜归属。
            foreach (var kindPair in unresolvedByKind)
            {
                foreach (var group in kindPair.Value)
                {
                    var firstReading = group.First();
                    if (!HardwareDisplayPolicy.IsUserVisible(firstReading.Device))
                    {
                        continue;
                    }

                    var providerTag = firstReading.Source.ToString();
                    result.Add(new HardwareTelemetryDeviceView(
                        firstReading.Device.DeviceKey,
                        firstReading.Device.Kind,
                        firstReading.Device.DisplayName + "（" + providerTag + "）",
                        false,
                        BuildRows(group, metricLabel, formatValue, sourceDisplay)));
                }
            }

            return result.OrderBy(card => OrderOf(card.Kind)).ToArray();
        }

        private static List<DisplayMetricRow> BuildRows(
            IEnumerable<TelemetryReading> readings,
            Func<TelemetryMetricKey, string> metricLabel,
            Func<double, TelemetryUnit, string> formatValue,
            Func<TelemetrySourceKind, string> sourceDisplay)
        {
            return readings
                .GroupBy(reading => reading.MetricKey.Value)
                .Select(group => group.First())
                .OrderBy(reading => reading.MetricKey.Value, StringComparer.Ordinal)
                .Select(reading => new DisplayMetricRow(
                    metricLabel(reading.MetricKey),
                    formatValue(reading.Value, reading.Unit),
                    sourceDisplay(reading.Source),
                    "stat:" + reading.Device.DeviceKey + ":" + reading.MetricKey.Value))
                .ToList();
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


