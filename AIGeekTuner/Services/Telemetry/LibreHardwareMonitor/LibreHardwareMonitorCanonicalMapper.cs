using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.LibreHardwareMonitor
{
    /// <summary>
    /// LHM Raw → canonical 映射。规则边界（禁止泛化 label 猜测）：
    /// 1) 先按结构信息（设备类别 + 原生单位）圈定候选；
    /// 2) 只在候选内使用 V1 已验证的受限偏好名序列；
    /// 3) 无法可靠归属的读数保留为 Raw，绝不进入 canonical；
    /// 4) 不推算、不估算缺失指标。
    /// </summary>
    public static class LibreHardwareMonitorCanonicalMapper
    {
        private const double MaxPlausibleTemperatureCelsius = 250;

        public static IReadOnlyList<TelemetryReading> Map(
            IReadOnlyList<RawTelemetryReading> rawReadings)
        {
            var result = new List<TelemetryReading>();

            MapCpu(rawReadings, result);
            MapGpus(rawReadings, result);
            MapMemory(rawReadings, result);
            MapStorage(rawReadings, result);

            return result;
        }

        private static void MapCpu(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            var cpu = FilterByDeviceKind(rawReadings, TelemetryDeviceKind.Cpu);

            var packageTemperature = SelectPreferred(
                cpu,
                TelemetryUnit.Celsius,
                ["Package", "Core Max", "Tctl", "Tdie"],
                requirePositiveValue: true,
                maxValue: MaxPlausibleTemperatureCelsius);
            AddIfFound(result, packageTemperature, TelemetryMetricKey.CpuPackageTemperature);

            var totalLoad = SelectPreferred(
                cpu,
                TelemetryUnit.Percent,
                ["CPU Total", "Total"],
                requirePositiveValue: false,
                maxValue: 100);
            AddIfFound(result, totalLoad, TelemetryMetricKey.CpuTotalUtilization);

            var clock = SelectPreferred(
                cpu,
                TelemetryUnit.Megahertz,
                ["Core Average", "Core #1"],
                requirePositiveValue: true,
                maxValue: double.MaxValue);
            AddIfFound(result, clock, TelemetryMetricKey.CpuClock);

            var packagePower = SelectPreferred(
                cpu,
                TelemetryUnit.Watt,
                ["Package", "CPU Package"],
                requirePositiveValue: true,
                maxValue: double.MaxValue);
            AddIfFound(result, packagePower, TelemetryMetricKey.CpuPackagePower);
        }

        private static void MapGpus(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            foreach (var group in rawReadings
                .Where(reading => reading.Device.Kind == TelemetryDeviceKind.Gpu)
                .GroupBy(reading => reading.Device.DeviceKey))
            {
                var gpu = group.ToArray();

                var coreTemperature = SelectPreferred(
                    gpu,
                    TelemetryUnit.Celsius,
                    ["GPU Core", "Core"],
                    requirePositiveValue: true,
                    maxValue: MaxPlausibleTemperatureCelsius);
                AddIfFound(result, coreTemperature, TelemetryMetricKey.GpuCoreTemperature);

                var utilization = SelectPreferred(
                    gpu,
                    TelemetryUnit.Percent,
                    ["GPU Core", "Core"],
                    requirePositiveValue: false,
                    maxValue: 100);
                AddIfFound(result, utilization, TelemetryMetricKey.GpuCoreUtilization);

                var coreClock = SelectPreferred(
                    gpu,
                    TelemetryUnit.Megahertz,
                    ["GPU Core", "Core"],
                    requirePositiveValue: true,
                    maxValue: double.MaxValue);
                AddIfFound(result, coreClock, TelemetryMetricKey.GpuCoreClock);

                var boardPower = SelectPreferred(
                    gpu,
                    TelemetryUnit.Watt,
                    ["GPU Package", "Package", "GPU Power"],
                    requirePositiveValue: true,
                    maxValue: double.MaxValue);
                AddIfFound(result, boardPower, TelemetryMetricKey.GpuBoardPower);
            }
        }

        private static void MapMemory(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            var memory = FilterByDeviceKind(rawReadings, TelemetryDeviceKind.Memory);

            var load = SelectPreferred(
                memory,
                TelemetryUnit.Percent,
                ["Memory", "Load"],
                requirePositiveValue: false,
                maxValue: 100);
            AddIfFound(result, load, TelemetryMetricKey.MemoryUtilization);

            // 仅接受 LHM 的 GB 量级 “Used Memory”（LHM 的 GB 即 GiB）；其余保持 Raw。
            var used = memory.FirstOrDefault(reading =>
                reading.Unit == TelemetryUnit.Gigabyte
                && reading.Label.Equals("Used Memory", StringComparison.OrdinalIgnoreCase)
                && reading.Value >= 0);
            if (used is not null)
            {
                result.Add(new TelemetryReading(
                    TelemetryMetricKey.MemoryUsed,
                    TelemetryUnitConversion.GibibytesToBytes(used.Value),
                    TelemetryUnit.Byte,
                    used.Device,
                    used.Source,
                    used.SourceMetricId,
                    used.Label,
                    used.CapturedAtUtc));
            }
        }

        private static void MapStorage(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            ICollection<TelemetryReading> result)
        {
            foreach (var group in rawReadings
                .Where(reading => reading.Device.Kind == TelemetryDeviceKind.Storage)
                .GroupBy(reading => reading.Device.DeviceKey))
            {
                var temperature = group
                    .Where(reading =>
                        reading.Unit == TelemetryUnit.Celsius
                        && reading.Value > 0
                        && reading.Value <= MaxPlausibleTemperatureCelsius)
                    .OrderByDescending(reading => reading.Value)
                    .FirstOrDefault();
                AddIfFound(result, temperature, TelemetryMetricKey.StorageTemperature);
            }
        }

        private static RawTelemetryReading[] FilterByDeviceKind(
            IReadOnlyList<RawTelemetryReading> rawReadings,
            TelemetryDeviceKind kind)
        {
            return rawReadings
                .Where(reading => reading.Device.Kind == kind)
                .ToArray();
        }

        private static RawTelemetryReading? SelectPreferred(
            IReadOnlyList<RawTelemetryReading> readings,
            TelemetryUnit unit,
            IReadOnlyList<string> preferredNames,
            bool requirePositiveValue,
            double maxValue)
        {
            var candidates = readings
                .Where(reading =>
                    reading.Unit == unit
                    && TelemetryUnitConversion.IsFinite(reading.Value)
                    && (!requirePositiveValue || reading.Value > 0)
                    && reading.Value <= maxValue)
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            foreach (var preferredName in preferredNames)
            {
                var preferred = candidates.FirstOrDefault(reading =>
                    reading.Label.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
                if (preferred is not null)
                {
                    return preferred;
                }
            }

            return candidates.OrderByDescending(reading => reading.Value).First();
        }

        private static void AddIfFound(
            ICollection<TelemetryReading> result,
            RawTelemetryReading? reading,
            TelemetryMetricKey metricKey)
        {
            if (reading is null)
            {
                return;
            }

            result.Add(new TelemetryReading(
                metricKey,
                reading.Value,
                reading.Unit,
                reading.Device,
                reading.Source,
                reading.SourceMetricId,
                reading.Label,
                reading.CapturedAtUtc));
        }
    }
}
