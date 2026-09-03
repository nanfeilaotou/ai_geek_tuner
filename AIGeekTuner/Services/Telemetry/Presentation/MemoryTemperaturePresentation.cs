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

        /// <summary>V2-M4.5C Gate I：带用户可读标签的单模块温度。</summary>
    public sealed record ModuleTemperatureLabel(
        string ModuleKey,
        string Label,
        double ValueCelsius);

    /// <summary>
    /// V2-M4.5C Gate I：把 resolved 模块温度映射为用户可读名称。
    /// 优先用 HWiNFO DDR5 传感器提供的 SMBIOS DeviceLocator 强 ID 对齐静态
    /// inventory 的 DeviceLocator 顺序 → “DIMM 1 / DIMM 2”；无法可靠映射时
    /// 使用安全占位名（“内存模块 A/B…”），绝不显示 memory-module:N 源键，
    /// 也不在无证据时伪造 DIMM 序号。
    /// </summary>
    public static IReadOnlyList<ModuleTemperatureLabel> DescribeWithLabels(
        TelemetrySnapshot snapshot,
        IReadOnlyList<Models.Hardware.Inventory.MemoryModuleInfo>? staticMemoryModules)
    {
        var rows = CollectCurrentRows(snapshot);
        if (rows.Count == 0)
        {
            return [];
        }

        // 源本地模块 → DeviceLocator 强 ID。canonical 键形如 src:{source}:{nativeId}
        // （未合并）或合并组键；用 Raw 层 (Source, NativeDeviceId) 重建映射。
        var locatorByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in snapshot.RawReadings)
        {
            if (raw.Device.Kind != TelemetryDeviceKind.MemoryModule)
            {
                continue;
            }

            var locator = raw.DeviceInfo.StrongIds.FirstOrDefault(strongId =>
                strongId.StartsWith("locator:", StringComparison.Ordinal));
            if (locator is null)
            {
                continue;
            }

            var value = locator["locator:".Length..];
            locatorByKey.TryAdd(raw.Device.DeviceKey, value);
            locatorByKey.TryAdd(
                "src:" + raw.Source + ":" + raw.DeviceInfo.NativeDeviceId,
                value);
        }

        var fallbackIndex = 0;
        var result = new List<ModuleTemperatureLabel>(rows.Count);
        foreach (var row in rows)
        {
            string label;
            if (locatorByKey.TryGetValue(row.ModuleKey, out var locator)
                && staticMemoryModules is not null)
            {
                var index = -1;
                for (var i = 0; i < staticMemoryModules.Count; i++)
                {
                    if (string.Equals(
                        staticMemoryModules[i].DeviceLocator,
                        locator,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                label = index >= 0 ? "DIMM " + (index + 1) : FallbackLabel(fallbackIndex++);
            }
            else
            {
                label = FallbackLabel(fallbackIndex++);
            }

            result.Add(new ModuleTemperatureLabel(row.ModuleKey, label, row.ValueCelsius));
        }

        return result;

        static string FallbackLabel(int index)
        {
            const string letters = "ABCDEFGH";
            return index < letters.Length
                ? "内存模块 " + letters[index]
                : "内存模块 " + (index + 1);
        }
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
