using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Presentation
{
    /// <summary>V2-M4.5B Gate G：单根内存模块的当前温度读数（presentation-ready）。</summary>
    public sealed record ModuleTemperatureRow(
        string ModuleKey,
        string ModuleDisplayName,
        double ValueCelsius,
        TelemetrySourceKind Source,
        string SourceMetricId,
        string? SourceLabel,
        DateTimeOffset CapturedAtUtc);

    /// <summary>
    /// V2-M4.5B Gate G：内存模块温度的纯展示辅助。
    /// 只读 <see cref="TelemetrySnapshot.CanonicalReadings"/> 中已 resolved 的
    /// memory.module.temperature 读数；不新增任何 global canonical metric，
    /// 不做任何阈值判断。SourceLabel 保留原始语义（如 HWiNFO 的
    /// "SPD Hub Temperature"——DDR5 下测的是 SPD Hub 芯片，不是 DRAM 结温）。
    /// </summary>
    public static class MemoryTemperaturePresentation
    {
        public static IReadOnlyList<ModuleTemperatureRow> CollectCurrentRows(
            TelemetrySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            return snapshot.CanonicalReadings
                .Where(reading =>
                    reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature)
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(reading => new ModuleTemperatureRow(
                    reading.Device.DeviceKey,
                    reading.Device.DisplayName,
                    reading.Value,
                    reading.Source,
                    reading.SourceMetricId,
                    reading.SourceLabel,
                    reading.CapturedAtUtc))
                .OrderBy(row => row.ModuleKey, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 当前所有 resolved memory.module.temperature 中的最大值；
        /// 没有任何模块温度读数时返回 null。仅用于 UI 摘要（M4.5C）。
        /// </summary>
        public static double? CurrentHighestModuleTemperature(TelemetrySnapshot snapshot)
        {
            var rows = CollectCurrentRows(snapshot);
            return rows.Count == 0 ? null : rows.Max(row => row.ValueCelsius);
        }
    }
}
