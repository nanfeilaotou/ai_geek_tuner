using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Presentation
{
    /// <summary>meter 行：走 LiveMetricMeter 可视化标尺（scale 是显示标尺不是阈值）。</summary>
    public sealed record LiveMeterLine(
        string Label,
        double Current,
        double? Low,
        double? High,
        double ScaleMin,
        double ScaleMax,
        TelemetryUnit Unit);

    /// <summary>numeric 行：不画 progress bar，仅 当前/低/高 文本。</summary>
    public sealed record LiveNumericLine(
        string Label,
        string Current,
        string? Low,
        string? High);

    /// <summary>meter/numeric 下方的次级小文本（Hotspot、DIMM 温度等）。</summary>
    public sealed record LiveSubLine(string Text, bool IsSecondary = true);

    /// <summary>Hardware 右侧一张实时设备卡。</summary>
    public sealed record LiveDeviceCard(
        string DeviceKey,
        string Title,
        IReadOnlyList<LiveMeterLine> Meters,
        IReadOnlyList<LiveNumericLine> Numerics,
        IReadOnlyList<LiveSubLine> SubLines);

    /// <summary>
    /// V2-M4.5C Gate F：Hardware 右侧实时区装配（Meter + Current/Low/High）。
    /// 范围一律来自 <see cref="LiveMetricRangeTracker"/>（来源无关，不用 HWiNFO
    /// native Min/Max）。Visual scale：usage 0-100；CPU/GPU 温度 0-110；
    /// 内存/盘温 0-100——只是显示标尺，不是安全阈值。
    /// </summary>
    public static class HardwareLiveViewBuilder
    {
        public static IReadOnlyList<LiveDeviceCard> Build(
            TelemetrySnapshot snapshot,
            LiveMetricRangeTracker ranges,
            IReadOnlyList<MemoryModuleInfo>? staticMemoryModules = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(ranges);

            var cards = new List<LiveDeviceCard>
            {
                BuildCpu(snapshot, ranges),
            };

            cards.AddRange(BuildGpus(snapshot, ranges));
            cards.Add(BuildMemory(snapshot, ranges, staticMemoryModules));
            cards.AddRange(BuildStorage(snapshot, ranges));

            return cards.Where(card => card.Meters.Count > 0 || card.Numerics.Count > 0).ToList();
        }

        // ------------------------------------------------------------------ CPU
        private static LiveDeviceCard BuildCpu(TelemetrySnapshot snapshot, LiveMetricRangeTracker ranges)
        {
            // canonical 键可能经 Reconciler 合并（如 cpu:singleton）——按 Kind 选组。
            var cpuGroup = snapshot.CanonicalReadings
                .Where(reading => reading.Device.Kind == TelemetryDeviceKind.Cpu)
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .FirstOrDefault();
            var deviceKey = cpuGroup?.Key ?? "cpu";
            var byMetric = (cpuGroup ?? Enumerable.Empty<TelemetryReading>())
                .GroupBy(reading => reading.MetricKey.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var meters = new List<LiveMeterLine>();
            var numerics = new List<LiveNumericLine>();

            var packageTemp = Get(byMetric, TelemetryMetricKey.CpuPackageTemperature);
            if (packageTemp is not null)
            {
                meters.Add(Meter("温度", deviceKey, TelemetryMetricKey.CpuPackageTemperature,
                    packageTemp.Value, TelemetryUnit.Celsius, LiveMetricMeterMath.CpuGpuTemperatureScaleMax, ranges));
            }

            var utilization = Get(byMetric, TelemetryMetricKey.CpuTotalUtilization);
            if (utilization is not null)
            {
                meters.Add(Meter("使用率", deviceKey, TelemetryMetricKey.CpuTotalUtilization,
                    utilization.Value, TelemetryUnit.Percent, LiveMetricMeterMath.UsageScaleMax, ranges));
            }

            var clock = Get(byMetric, TelemetryMetricKey.CpuClock);
            if (clock is not null)
            {
                numerics.Add(Numeric("核心频率", deviceKey, TelemetryMetricKey.CpuClock,
                    clock.Value, TelemetryUnit.Megahertz, ranges));
            }

            var power = Get(byMetric, TelemetryMetricKey.CpuPackagePower);
            if (power is not null)
            {
                numerics.Add(Numeric("Package Power", deviceKey, TelemetryMetricKey.CpuPackagePower,
                    power.Value, TelemetryUnit.Watt, ranges));
            }

            return new LiveDeviceCard(deviceKey, "CPU", meters, numerics, []);
        }

        // ------------------------------------------------------------------ GPU
        private static IEnumerable<LiveDeviceCard> BuildGpus(TelemetrySnapshot snapshot, LiveMetricRangeTracker ranges)
        {
            var gpuGroups = snapshot.CanonicalReadings
                .Where(reading => reading.Device.Kind == TelemetryDeviceKind.Gpu)
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                // 独显在前，iGPU 靠后（与既有排序语义一致）。
                .OrderBy(group => IsIntelIntegratedGpu(
                    group.First().Device.DisplayName) ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.Ordinal);

            foreach (var group in gpuGroups)
            {
                var deviceKey = group.Key;
                var device = group.First().Device;
                // 显示策略：匿名 GPU 不进主界面（§3，数据仍在 Raw 明细）。
                if (!HardwareDisplayPolicy.IsUserVisible(device))
                {
                    continue;
                }

                var isIntel = IsIntelIntegratedGpu(device.DisplayName);
                var byMetric = group
                    .GroupBy(reading => reading.MetricKey.Value, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                var meters = new List<LiveMeterLine>();
                var numerics = new List<LiveNumericLine>();
                var subLines = new List<LiveSubLine>();

                var coreTemp = Get(byMetric, TelemetryMetricKey.GpuCoreTemperature);
                var utilization = Get(byMetric, TelemetryMetricKey.GpuCoreUtilization);

                if (!isIntel)
                {
                    if (coreTemp is not null)
                    {
                        meters.Add(Meter("核心温度", deviceKey, TelemetryMetricKey.GpuCoreTemperature,
                            coreTemp.Value, TelemetryUnit.Celsius,
                            LiveMetricMeterMath.CpuGpuTemperatureScaleMax, ranges));
                    }

                    if (utilization is not null)
                    {
                        meters.Add(Meter("使用率", deviceKey, TelemetryMetricKey.GpuCoreUtilization,
                            utilization.Value, TelemetryUnit.Percent,
                            LiveMetricMeterMath.UsageScaleMax, ranges));
                    }

                    var coreClock = Get(byMetric, TelemetryMetricKey.GpuCoreClock);
                    if (coreClock is not null)
                    {
                        numerics.Add(Numeric("核心频率", deviceKey, TelemetryMetricKey.GpuCoreClock,
                            coreClock.Value, TelemetryUnit.Megahertz, ranges));
                    }

                    var memoryClock = Get(byMetric, TelemetryMetricKey.GpuMemoryClock);
                    if (memoryClock is not null)
                    {
                        numerics.Add(Numeric("显存频率", deviceKey, TelemetryMetricKey.GpuMemoryClock,
                            memoryClock.Value, TelemetryUnit.Megahertz, ranges));
                    }

                    var power = Get(byMetric, TelemetryMetricKey.GpuBoardPower);
                    if (power is not null)
                    {
                        numerics.Add(Numeric("功耗", deviceKey, TelemetryMetricKey.GpuBoardPower,
                            power.Value, TelemetryUnit.Watt, ranges));
                    }

                    // Hotspot / VRAM 温度：不做大 meter，次级小文本。
                    var hotspot = Get(byMetric, TelemetryMetricKey.GpuHotspotTemperature);
                    var vram = Get(byMetric, TelemetryMetricKey.GpuMemoryTemperature);
                    var parts = new List<string>();
                    if (hotspot is not null)
                    {
                        parts.Add("Hotspot " + FormatValue(hotspot.Value, TelemetryUnit.Celsius));
                    }

                    if (vram is not null)
                    {
                        parts.Add("显存 " + FormatValue(vram.Value, TelemetryUnit.Celsius));
                    }

                    if (parts.Count > 0)
                    {
                        subLines.Add(new LiveSubLine(string.Join(" · ", parts)));
                    }
                }
                else
                {
                    // iGPU：使用率必须有；温度只有可靠 resolved 才显示；numeric 核心频率。
                    if (utilization is not null)
                    {
                        meters.Add(Meter("使用率", deviceKey, TelemetryMetricKey.GpuCoreUtilization,
                            utilization.Value, TelemetryUnit.Percent,
                            LiveMetricMeterMath.UsageScaleMax, ranges));
                    }

                    if (coreTemp is not null)
                    {
                        meters.Add(Meter("温度", deviceKey, TelemetryMetricKey.GpuCoreTemperature,
                            coreTemp.Value, TelemetryUnit.Celsius,
                            LiveMetricMeterMath.CpuGpuTemperatureScaleMax, ranges));
                    }

                    var coreClock = Get(byMetric, TelemetryMetricKey.GpuCoreClock);
                    if (coreClock is not null)
                    {
                        numerics.Add(Numeric("核心频率", deviceKey, TelemetryMetricKey.GpuCoreClock,
                            coreClock.Value, TelemetryUnit.Megahertz, ranges));
                    }
                }

                // V2-M4.5C.1 Gate C：只显示 user-facing 短型号，
                // 绝不显示 source-local 前缀（如 HWiNFO “GPU [#1]: …”）。
                yield return new LiveDeviceCard(
                    deviceKey,
                    "GPU · " + ShortGpuName(device.DisplayName),
                    meters,
                    numerics,
                    subLines);
            }
        }

        /// <summary>§9：Intel iGPU 显示排序靠后（独立 iGPU 卡）。</summary>
        public static bool IsIntelIntegratedGpu(string displayName) =>
            displayName.Contains("intel", StringComparison.OrdinalIgnoreCase);

        private static string ShortGpuName(string displayName)
        {
            var name = StripSourceLocalPrefix(displayName);
            return name.Length <= 48 ? name : name[..48] + "…";
        }

        /// <summary>
        /// 剥掉来源自带的本地序号前缀（HWiNFO “GPU [#1]: NVIDIA …” → “NVIDIA …”）。
        /// 只影响显示，不改 canonical identity。
        /// </summary>
        public static string StripSourceLocalPrefix(string displayName) =>
            System.Text.RegularExpressions.Regex.Replace(
                displayName,
                "^[a-zA-Z]+\\s*\\[#\\d+\\]\\s*:?\\s*",
                string.Empty).Trim();

        // -------------------------------------------------------------- Memory
        private static LiveDeviceCard BuildMemory(
            TelemetrySnapshot snapshot,
            LiveMetricRangeTracker ranges,
            IReadOnlyList<MemoryModuleInfo>? staticMemoryModules)
        {
            // canonical 键可能经 Reconciler 合并（如 memory:singleton）——按 Kind 选组。
            var memoryGroup = snapshot.CanonicalReadings
                .Where(reading => reading.Device.Kind == TelemetryDeviceKind.Memory)
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .FirstOrDefault();
            var deviceKey = memoryGroup?.Key ?? "memory";
            var byMetric = (memoryGroup ?? Enumerable.Empty<TelemetryReading>())
                .GroupBy(reading => reading.MetricKey.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var meters = new List<LiveMeterLine>();
            var numerics = new List<LiveNumericLine>();
            var subLines = new List<LiveSubLine>();

            var utilization = Get(byMetric, TelemetryMetricKey.MemoryUtilization);
            if (utilization is not null)
            {
                meters.Add(Meter("使用率", deviceKey, TelemetryMetricKey.MemoryUtilization,
                    utilization.Value, TelemetryUnit.Percent, LiveMetricMeterMath.UsageScaleMax, ranges));
            }

            // 内存温度：resolved memory.module.temperature 中的当前最高值（Gate F/G）。
            var moduleLabels = MemoryTemperaturePresentation.DescribeWithLabels(
                snapshot, staticMemoryModules);
            var highest = ranges.GetHighestAcrossDevices(
                snapshot, TelemetryMetricKey.MemoryModuleTemperature);
            if (highest is not null)
            {
                meters.Add(new LiveMeterLine(
                    "内存温度",
                    highest.Current,
                    highest.Low,
                    highest.High,
                    0,
                    LiveMetricMeterMath.MemoryStorageTemperatureScaleMax,
                    TelemetryUnit.Celsius));
            }

            if (moduleLabels.Count > 0)
            {
                subLines.Add(new LiveSubLine(string.Join(" · ", moduleLabels.Select(module =>
                    module.Label + " " + FormatValue(module.ValueCelsius, TelemetryUnit.Celsius)))));
            }

            var used = Get(byMetric, TelemetryMetricKey.MemoryUsed);
            if (used is not null)
            {
                numerics.Add(Numeric("已用内存", deviceKey, TelemetryMetricKey.MemoryUsed,
                    used.Value, TelemetryUnit.Byte, ranges));
            }

            var clock = Get(byMetric, TelemetryMetricKey.MemoryClock);
            if (clock is not null)
            {
                numerics.Add(Numeric("内存频率", deviceKey, TelemetryMetricKey.MemoryClock,
                    clock.Value, TelemetryUnit.Megahertz, ranges));
            }

            return new LiveDeviceCard(deviceKey, "内存", meters, numerics, subLines);
        }

        // ------------------------------------------------------------- Storage
        private static IEnumerable<LiveDeviceCard> BuildStorage(TelemetrySnapshot snapshot, LiveMetricRangeTracker ranges)
        {
            var storageGroups = snapshot.CanonicalReadings
                .Where(reading => reading.Device.Kind == TelemetryDeviceKind.Storage
                    && reading.MetricKey == TelemetryMetricKey.StorageTemperature)
                .GroupBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .ToArray();
            // §5 显示策略：已有真实盘名时，匿名来源（大概率重复视图）不进主界面；
            // 无任何真实盘名时匿名盘回退“磁盘 #N”，绝不写 HDD（实际可能是 NVMe/SSD）。
            var hasNamedStorage = storageGroups.Any(group =>
                TelemetryDeviceReconciler.NormalizeName(group.First().Device.DisplayName).Length > 0);
            var anonymousOrdinal = 0;

            foreach (var group in storageGroups)
            {
                var deviceKey = group.Key;
                var reading = group.First();
                var isNamed = TelemetryDeviceReconciler.NormalizeName(reading.Device.DisplayName).Length > 0;
                if (hasNamedStorage && !isNamed)
                {
                    continue;
                }

                if (!isNamed)
                {
                    anonymousOrdinal++;
                }

                yield return new LiveDeviceCard(
                    deviceKey,
                    isNamed
                        ? "磁盘 · " + ShortStorageName(reading.Device.DisplayName)
                        : $"磁盘 #{anonymousOrdinal}",
                    [Meter("温度", deviceKey, TelemetryMetricKey.StorageTemperature,
                        reading.Value, TelemetryUnit.Celsius,
                        LiveMetricMeterMath.MemoryStorageTemperatureScaleMax, ranges)],
                    [],
                    []);
            }
        }

        private static string ShortStorageName(string displayName)
        {
            var name = StripSourceLocalPrefix(displayName);
            // 去掉常见 serial 括注（如 "Predator SSD GM7 (PSBH...)"）。
            var paren = name.IndexOf('(');
            if (paren > 0)
            {
                name = name[..paren].Trim();
            }

            return name.Length <= 32 ? name : name[..32] + "…";
        }

        // ------------------------------------------------------------- helpers
        private static TelemetryReading? Get(
            IReadOnlyDictionary<string, TelemetryReading> byMetric, TelemetryMetricKey metricKey) =>
            byMetric.TryGetValue(metricKey.Value, out var reading) ? reading : null;

        private static LiveMeterLine Meter(
            string label,
            string deviceKey,
            TelemetryMetricKey metricKey,
            double current,
            TelemetryUnit unit,
            double scaleMax,
            LiveMetricRangeTracker ranges)
        {
            var range = ranges.GetRange(deviceKey, metricKey);
            return new LiveMeterLine(label, current, range?.Low, range?.High, 0, scaleMax, unit);
        }

        private static LiveNumericLine Numeric(
            string label,
            string deviceKey,
            TelemetryMetricKey metricKey,
            double current,
            TelemetryUnit unit,
            LiveMetricRangeTracker ranges)
        {
            var range = ranges.GetRange(deviceKey, metricKey);
            return new LiveNumericLine(
                label,
                FormatValue(current, unit),
                range?.Low is null ? null : FormatValue(range.Low.Value, unit),
                range?.High is null ? null : FormatValue(range.High.Value, unit));
        }

        /// <summary>统一数值 formatter（Byte → GB，GiB 基准；与静态容量 formatter 一致）。</summary>
        public static string FormatValue(double value, TelemetryUnit unit) => unit switch
        {
            TelemetryUnit.Celsius => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value:0.#} °C"),
            TelemetryUnit.Watt => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value:0.#} W"),
            TelemetryUnit.Megahertz => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value:0} MHz"),
            TelemetryUnit.Percent => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value:0.#} %"),
            TelemetryUnit.Byte => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value / 1073741824d:0.##} GB"),
            TelemetryUnit.Gigabyte => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value:0.##} GB"),
            TelemetryUnit.Megabyte => string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{value:0.#} MB"),
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.###}"),
        };
    }
}