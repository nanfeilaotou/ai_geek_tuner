using System.Globalization;
using System.Text.RegularExpressions;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>
    /// AIDA64 Raw → canonical 映射。
    /// 只使用官方 Complete Sensor Value List 中的稳定 ID（§41：ID 优先于显示 label），
    /// 不做任何 Contains 式泛化猜测；未收录的 ID 永远停留在 Raw 层。
    /// 注意：官方导出清单不含功耗值，因此本来源绝不产生 power 指标（缺失即可，绝不制造）。
    /// </summary>
    public static partial class Aida64CanonicalMapper
    {
        private const double MaxPlausibleTemperatureCelsius = 250;

        [GeneratedRegex("^TGPU(\\d{1,2})$", RegexOptions.IgnoreCase)]
        private static partial Regex GpuCoreTemperature();

        [GeneratedRegex("^TGPU(\\d{1,2})HOT$", RegexOptions.IgnoreCase)]
        private static partial Regex GpuHotspotTemperature();

        [GeneratedRegex("^TGPU(\\d{1,2})MEM$", RegexOptions.IgnoreCase)]
        private static partial Regex GpuMemoryTemperatureRegex();

        [GeneratedRegex("^SGPU(\\d{1,2})CLK$", RegexOptions.IgnoreCase)]
        private static partial Regex GpuCoreClockRegex();

        [GeneratedRegex("^SGPU(\\d{1,2})UTI$", RegexOptions.IgnoreCase)]
        private static partial Regex GpuUtilizationRegex();

        [GeneratedRegex("^SGPU(\\d{1,2})USEDDEMEM$", RegexOptions.IgnoreCase)]
        private static partial Regex GpuDedicatedMemoryUsedRegex();

        [GeneratedRegex("^THDD(\\d{1,3})$", RegexOptions.IgnoreCase)]
        private static partial Regex StorageTemperatureRegex();

        // CPU 温度候选的官方 ID 优先级：Package > Tctl > 通用 CPU。
        private static readonly IReadOnlyList<string> CpuTemperatureIdPriority =
        [
            "TCPUPKG",
            "TCPUTCTL",
            "TCPU"
        ];

        public static IReadOnlyList<TelemetryReading> Map(
            IReadOnlyList<RawTelemetryReading> rawReadings)
        {
            var result = new List<TelemetryReading>();

            MapCpu(rawReadings, result);
            MapMemory(rawReadings, result);
            MapByPattern(rawReadings, result);

            return result;
        }

        private static void MapCpu(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            var cpu = rawReadings
                .Where(reading =>
                    reading.Device.Kind == TelemetryDeviceKind.Cpu
                    && reading.Unit == TelemetryUnit.Celsius
                    && reading.Value > 0
                    && reading.Value <= MaxPlausibleTemperatureCelsius)
                .GroupBy(reading => reading.SourceMetricId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var id in CpuTemperatureIdPriority)
            {
                if (cpu.TryGetValue(id, out var reading))
                {
                    result.Add(ToCanonical(reading, TelemetryMetricKey.CpuPackageTemperature));
                    break;
                }
            }

            AddExact(
                rawReadings,
                result,
                "SCPUUTI",
                TelemetryMetricKey.CpuTotalUtilization,
                TelemetryUnit.Percent,
                value => value is >= 0 and <= 100);
            AddExact(
                rawReadings,
                result,
                "SCPUTHR",
                TelemetryMetricKey.CpuThrottling,
                TelemetryUnit.Percent,
                value => value is >= 0 and <= 100);
            AddExact(
                rawReadings,
                result,
                "SCPUCLK",
                TelemetryMetricKey.CpuClock,
                TelemetryUnit.Megahertz,
                value => value >= 0);
        }

        private static void MapMemory(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            AddExact(
                rawReadings,
                result,
                "SMEMUTI",
                TelemetryMetricKey.MemoryUtilization,
                TelemetryUnit.Percent,
                value => value is >= 0 and <= 100);

            // 官方 “Used Memory” 以 MB 报告，统一转换为 Byte。
            var used = rawReadings.FirstOrDefault(reading =>
                string.Equals(reading.SourceMetricId, "SUSEDMEM", StringComparison.OrdinalIgnoreCase)
                && reading.Unit == TelemetryUnit.Megabyte
                && reading.Value >= 0);
            if (used is not null)
            {
                result.Add(new TelemetryReading(
                    TelemetryMetricKey.MemoryUsed,
                    TelemetryUnitConversion.MegabytesToBytes(used.Value),
                    TelemetryUnit.Byte,
                    used.Device,
                    used.Source,
                    used.SourceMetricId,
                    used.Label,
                    used.CapturedAtUtc));
            }

            AddExact(
                rawReadings,
                result,
                "SMEMCLK",
                TelemetryMetricKey.MemoryClock,
                TelemetryUnit.Megahertz,
                value => value >= 0);
        }

        private static void MapByPattern(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            foreach (var reading in rawReadings)
            {
                var id = reading.SourceMetricId;

                var coreTemp = GpuCoreTemperature().Match(id);
                if (coreTemp.Success)
                {
                    AddGpuIfPlausible(
                        result, reading, coreTemp.Groups[1].Value,
                        TelemetryMetricKey.GpuCoreTemperature,
                        TelemetryUnit.Celsius,
                        value => value > 0 && value <= MaxPlausibleTemperatureCelsius);
                    continue;
                }

                var hotspot = GpuHotspotTemperature().Match(id);
                if (hotspot.Success)
                {
                    AddGpuIfPlausible(
                        result, reading, hotspot.Groups[1].Value,
                        TelemetryMetricKey.GpuHotspotTemperature,
                        TelemetryUnit.Celsius,
                        value => value > 0 && value <= MaxPlausibleTemperatureCelsius);
                    continue;
                }

                var memoryTemp = GpuMemoryTemperatureRegex().Match(id);
                if (memoryTemp.Success)
                {
                    AddGpuIfPlausible(
                        result, reading, memoryTemp.Groups[1].Value,
                        TelemetryMetricKey.GpuMemoryTemperature,
                        TelemetryUnit.Celsius,
                        value => value > 0 && value <= MaxPlausibleTemperatureCelsius);
                    continue;
                }

                var coreClock = GpuCoreClockRegex().Match(id);
                if (coreClock.Success)
                {
                    AddGpuIfPlausible(
                        result, reading, coreClock.Groups[1].Value,
                        TelemetryMetricKey.GpuCoreClock,
                        TelemetryUnit.Megahertz,
                        value => value >= 0);
                    continue;
                }

                var utilization = GpuUtilizationRegex().Match(id);
                if (utilization.Success)
                {
                    AddGpuIfPlausible(
                        result, reading, utilization.Groups[1].Value,
                        TelemetryMetricKey.GpuCoreUtilization,
                        TelemetryUnit.Percent,
                        value => value is >= 0 and <= 100);
                    continue;
                }

                var dedicatedMemory = GpuDedicatedMemoryUsedRegex().Match(id);
                if (dedicatedMemory.Success)
                {
                    AddGpuMemoryUsed(result, reading, dedicatedMemory.Groups[1].Value);
                    continue;
                }

                var storage = StorageTemperatureRegex().Match(id);
                if (storage.Success)
                {
                    var indexText = storage.Groups[1].Value;
                    if (!int.TryParse(indexText, out var index) || index < 1 || index > 50)
                    {
                        continue; // 官方清单定义 HDD1..HDD50
                    }

                    if (reading.Unit == TelemetryUnit.Celsius
                        && reading.Value > 0
                        && reading.Value <= MaxPlausibleTemperatureCelsius)
                    {
                        result.Add(new TelemetryReading(
                            TelemetryMetricKey.StorageTemperature,
                            reading.Value,
                            TelemetryUnit.Celsius,
                            TelemetryDeviceIdentity.Storage($"hdd:{index}", $"HDD #{index}"),
                            reading.Source,
                            reading.SourceMetricId,
                            reading.Label,
                            reading.CapturedAtUtc));
                    }
                }
            }
        }

        private static void AddGpuIfPlausible(
            ICollection<TelemetryReading> result,
            RawTelemetryReading reading,
            string gpuIndexText,
            TelemetryMetricKey metricKey,
            TelemetryUnit expectedUnit,
            Func<double, bool> isValid)
        {
            if (!int.TryParse(gpuIndexText, out var gpuIndex)
                || gpuIndex < 1
                || gpuIndex > 12) // 官方清单定义 GPU1..GPU12
            {
                return;
            }

            if (reading.Unit != expectedUnit || !isValid(reading.Value))
            {
                return;
            }

            result.Add(new TelemetryReading(
                metricKey,
                reading.Value,
                expectedUnit,
                TelemetryDeviceIdentity.GpuByIndex(gpuIndex - 1, $"GPU #{gpuIndex}"),
                reading.Source,
                reading.SourceMetricId,
                reading.Label,
                reading.CapturedAtUtc));
        }

        private static void AddGpuMemoryUsed(
            ICollection<TelemetryReading> result,
            RawTelemetryReading reading,
            string gpuIndexText)
        {
            if (!int.TryParse(gpuIndexText, out var gpuIndex)
                || gpuIndex < 1
                || gpuIndex > 12)
            {
                return;
            }

            if (reading.Unit != TelemetryUnit.Megabyte || reading.Value < 0)
            {
                return;
            }

            result.Add(new TelemetryReading(
                TelemetryMetricKey.GpuMemoryUsed,
                TelemetryUnitConversion.MegabytesToBytes(reading.Value),
                TelemetryUnit.Byte,
                TelemetryDeviceIdentity.GpuByIndex(gpuIndex - 1, $"GPU #{gpuIndex}"),
                reading.Source,
                reading.SourceMetricId,
                reading.Label,
                reading.CapturedAtUtc));
        }

        private static void AddExact(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result,
            string sourceId,
            TelemetryMetricKey metricKey,
            TelemetryUnit unit,
            Func<double, bool> isValid)
        {
            var reading = rawReadings.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceMetricId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (reading is null || reading.Unit != unit || !isValid(reading.Value))
            {
                return;
            }

            result.Add(ToCanonical(reading, metricKey));
        }

        private static TelemetryReading ToCanonical(
            RawTelemetryReading reading,
            TelemetryMetricKey metricKey) =>
            new(
                metricKey,
                reading.Value,
                reading.Unit,
                reading.Device,
                reading.Source,
                reading.SourceMetricId,
                reading.Label,
                reading.CapturedAtUtc);

        /// <summary>
        /// 官方 ID → （父设备, 原生单位）分类。返回 false 表示完全未收录的 ID
        /// （仍可作为 Raw 保留，挂到 system 设备上）。模式事实源只有本类。
        /// </summary>
        public static bool TryClassifyDeviceAndUnit(
            string id,
            out TelemetryDeviceIdentity device,
            out TelemetryUnit unit)
        {
            switch (id)
            {
                case "TCPU":
                case "TCPUPKG":
                case "TCPUTCTL":
                case "SCPUUTI":
                case "SCPUTHR":
                case "SCPUCLK":
                    device = TelemetryDeviceIdentity.Cpu("CPU");
                    unit = ClassifyCpuUnit(id);
                    return true;

                case "SMEMUTI":
                case "SUSEDMEM":
                case "SMEMCLK":
                    device = TelemetryDeviceIdentity.Memory("Memory");
                    unit = id == "SUSEDMEM" ? TelemetryUnit.Megabyte : TelemetryUnit.Percent;
                    if (id == "SMEMCLK")
                    {
                        unit = TelemetryUnit.Megahertz;
                    }

                    return true;
            }

            var gpuIndex = MatchGpuIndex(id);
            if (gpuIndex > 0)
            {
                device = TelemetryDeviceIdentity.GpuByIndex(gpuIndex - 1, $"GPU #{gpuIndex}");
                unit = id.EndsWith("MEM", StringComparison.OrdinalIgnoreCase)
                        || id.EndsWith("HOT", StringComparison.OrdinalIgnoreCase)
                    ? TelemetryUnit.Celsius
                    : id.StartsWith("TGPU", StringComparison.OrdinalIgnoreCase)
                        ? TelemetryUnit.Celsius
                        : id.Contains("USEDDEMEM", StringComparison.OrdinalIgnoreCase)
                            ? TelemetryUnit.Megabyte
                            : id.EndsWith("UTI", StringComparison.OrdinalIgnoreCase)
                                ? TelemetryUnit.Percent
                                : TelemetryUnit.Megahertz;
                return true;
            }

            var hdd = StorageTemperatureRegex().Match(id);
            if (hdd.Success
                && int.TryParse(hdd.Groups[1].Value, out var hddIndex))
            {
                device = TelemetryDeviceIdentity.Storage($"hdd:{hddIndex}", $"HDD #{hddIndex}");
                unit = TelemetryUnit.Celsius;
                return true;
            }

            device = null!;
            unit = TelemetryUnit.None;
            return false;
        }

        private static TelemetryUnit ClassifyCpuUnit(string id) =>
            id switch
            {
                "SCPUUTI" or "SCPUTHR" => TelemetryUnit.Percent,
                "SCPUCLK" => TelemetryUnit.Megahertz,
                _ => TelemetryUnit.Celsius
            };

        /// <summary>从 GPU 系列 ID 中提取 1..12 的序号；非 GPU 系列返回 0。</summary>
        private static int MatchGpuIndex(string id)
        {
            foreach (var candidate in new[]
                     {
                         GpuCoreTemperature().Match(id),
                         GpuHotspotTemperature().Match(id),
                         GpuMemoryTemperatureRegex().Match(id),
                         GpuCoreClockRegex().Match(id),
                         GpuUtilizationRegex().Match(id),
                         GpuDedicatedMemoryUsedRegex().Match(id),
                         Regex.Match(id, "^SGPU(\\d{1,2})MEMCLK$", RegexOptions.IgnoreCase)
                     })
            {
                if (candidate.Success && candidate.Groups[1].Success)
                {
                    return int.Parse(candidate.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }

            return 0;
        }
    }
}
