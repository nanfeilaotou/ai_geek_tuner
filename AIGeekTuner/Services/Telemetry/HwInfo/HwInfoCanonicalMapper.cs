using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>
    /// HWiNFO 内部条目 → canonical 的有界映射（休眠代码：M1 无合法读取器时不会产生数据）。
    /// 规则约束：父传感器名按前缀分族（CPU / GPU / RAM·Memory）；读数 label 必须精确匹配
    /// 白名单（trim 后 OrdinalIgnoreCase）；单位文本必须命中精确集合。任何不满足者保持 Raw。
    /// 该表必须在接入真实读取器后用真实字符串复核后再启用。
    /// </summary>
    public static class HwInfoCanonicalMapper
    {
        private const double MaxPlausibleTemperatureCelsius = 250;

        public static IReadOnlyList<TelemetryReading> Map(
            IReadOnlyList<HwInfoSensorEntry> sensors,
            IReadOnlyList<HwInfoReadingEntry> readings,
            DateTimeOffset capturedAtUtc)
        {
            var result = new List<TelemetryReading>();
            var nameByIndex = sensors.ToDictionary(
                sensor => sensor.SensorIndex,
                sensor => sensor.SensorName);

            var gpuOrdinals = sensors
                .Where(sensor => IsFamily(sensor.SensorName, "GPU"))
                .Select(sensor => sensor.SensorIndex)
                .Distinct()
                .OrderBy(index => index)
                .Select((index, ordinal) => (index, ordinal))
                .ToDictionary(pair => pair.index, pair => pair.ordinal);

            foreach (var reading in readings)
            {
                if (!nameByIndex.TryGetValue(reading.SensorIndex, out var sensorName))
                {
                    continue; // 没有父传感器的孤儿读数：只可能来自损坏数据，拒绝。
                }

                var unit = ResolveUnit(reading);
                var label = reading.Label.Trim();

                // V2-M4.5B Gate D：DDR5 per-module SPD Hub Temperature。
                // 必须同时满足：父传感器明确是模块传感器（RAM Module #N / DIMM N）
                // + label 精确匹配 + 摄氏度 + 数值合理；原始 label 原样保留在 SourceLabel。
                if (unit == TelemetryUnit.Celsius
                    && LabelEquals(label, MemoryModuleSensorNames.SpdHubTemperatureLabel)
                    && MemoryModuleSensorNames.TryGetModuleIndex(sensorName, out var moduleIndex)
                    && MemoryModuleSensorNames.IsPlausibleTemperature(reading.Value))
                {
                    result.Add(new TelemetryReading(
                        TelemetryMetricKey.MemoryModuleTemperature,
                        reading.Value,
                        TelemetryUnit.Celsius,
                        TelemetryDeviceIdentity.MemoryModule(
                            $"memory-module:{moduleIndex}", sensorName),
                        TelemetrySourceKind.HwInfo,
                        CompositeSourceId(reading),
                        reading.Label,
                        capturedAtUtc));
                    continue;
                }

                if (IsFamily(sensorName, "CPU"))
                {
                    MapCpuReading(result, reading, label, unit, capturedAtUtc);
                }
                else if (IsFamily(sensorName, "GPU")
                    && gpuOrdinals.TryGetValue(reading.SensorIndex, out var ordinal))
                {
                    MapGpuReading(
                        result, reading, label, unit,
                        TelemetryDeviceIdentity.GpuByIndex(ordinal, sensorName),
                        capturedAtUtc);
                }
                else if (IsFamily(sensorName, "RAM") || IsFamily(sensorName, "Memory"))
                {
                    MapMemoryReading(result, reading, label, unit, capturedAtUtc);
                }

                // 其余家族（主板、盘、芯片组等）本轮一律保持 Raw，不做猜测性映射。
            }

            return result;
        }

        private static void MapCpuReading(
            ICollection<TelemetryReading> result,
            HwInfoReadingEntry reading,
            string label,
            TelemetryUnit unit,
            DateTimeOffset capturedAtUtc)
        {
            var device = TelemetryDeviceIdentity.Cpu("CPU");

            if (unit == TelemetryUnit.Celsius
                && LabelEquals(label, "CPU Package")
                && IsValidTemperature(reading.Value))
            {
                Add(result, TelemetryMetricKey.CpuPackageTemperature, reading, unit, device, capturedAtUtc);
            }
            else if (unit == TelemetryUnit.Watt
                && (LabelEquals(label, "CPU Package Power") || LabelEquals(label, "Package Power"))
                && reading.Value > 0)
            {
                Add(result, TelemetryMetricKey.CpuPackagePower, reading, unit, device, capturedAtUtc);
            }
            else if (unit == TelemetryUnit.Percent
                && (LabelEquals(label, "CPU Total") || LabelEquals(label, "Total CPU Usage"))
                && reading.Value is >= 0 and <= 100)
            {
                Add(result, TelemetryMetricKey.CpuTotalUtilization, reading, unit, device, capturedAtUtc);
            }
        }

        private static void MapGpuReading(
            ICollection<TelemetryReading> result,
            HwInfoReadingEntry reading,
            string label,
            TelemetryUnit unit,
            TelemetryDeviceIdentity device,
            DateTimeOffset capturedAtUtc)
        {
            switch (unit)
            {
                case TelemetryUnit.Celsius when IsValidTemperature(reading.Value):
                    if (LabelEquals(label, "GPU Core") || LabelEquals(label, "GPU Temperature"))
                    {
                        Add(result, TelemetryMetricKey.GpuCoreTemperature, reading, unit, device, capturedAtUtc);
                    }
                    else if (LabelEquals(label, "GPU Hot Spot") || LabelEquals(label, "GPU Hotspot"))
                    {
                        Add(result, TelemetryMetricKey.GpuHotspotTemperature, reading, unit, device, capturedAtUtc);
                    }
                    else if (LabelEquals(label, "GPU Memory Temperature"))
                    {
                        Add(result, TelemetryMetricKey.GpuMemoryTemperature, reading, unit, device, capturedAtUtc);
                    }

                    break;

                case TelemetryUnit.Watt when reading.Value > 0
                    && (LabelEquals(label, "GPU Power")
                        || LabelEquals(label, "GPU Package Power")
                        || LabelEquals(label, "Board Power Draw")):
                    Add(result, TelemetryMetricKey.GpuBoardPower, reading, unit, device, capturedAtUtc);
                    break;

                case TelemetryUnit.Percent when reading.Value is >= 0 and <= 100
                    && LabelEquals(label, "GPU Utilization"):
                    Add(result, TelemetryMetricKey.GpuCoreUtilization, reading, unit, device, capturedAtUtc);
                    break;

                case TelemetryUnit.Megahertz when reading.Value >= 0
                    && LabelEquals(label, "GPU Core Clock"):
                    Add(result, TelemetryMetricKey.GpuCoreClock, reading, unit, device, capturedAtUtc);
                    break;

                case TelemetryUnit.Megabyte or TelemetryUnit.Gigabyte when reading.Value >= 0
                    && LabelEquals(label, "GPU Memory Used"):
                    Add(
                        result,
                        TelemetryMetricKey.GpuMemoryUsed,
                        reading,
                        TelemetryUnit.Byte,
                        device,
                        capturedAtUtc,
                        ToBytes(reading.Value, unit));
                    break;
            }
        }

        private static void MapMemoryReading(
            ICollection<TelemetryReading> result,
            HwInfoReadingEntry reading,
            string label,
            TelemetryUnit unit,
            DateTimeOffset capturedAtUtc)
        {
            var device = TelemetryDeviceIdentity.Memory("Memory");

            if (unit is TelemetryUnit.Megabyte or TelemetryUnit.Gigabyte
                && reading.Value >= 0
                && (LabelEquals(label, "Used Memory") || LabelEquals(label, "Memory Used")))
            {
                Add(
                    result,
                    TelemetryMetricKey.MemoryUsed,
                    reading,
                    TelemetryUnit.Byte,
                    device,
                    capturedAtUtc,
                    ToBytes(reading.Value, unit));
            }
            else if (unit == TelemetryUnit.Percent
                && reading.Value is >= 0 and <= 100
                && LabelEquals(label, "Memory Usage"))
            {
                Add(result, TelemetryMetricKey.MemoryUtilization, reading, unit, device, capturedAtUtc);
            }
        }

        private static void Add(
            ICollection<TelemetryReading> result,
            TelemetryMetricKey metricKey,
            HwInfoReadingEntry reading,
            TelemetryUnit unit,
            TelemetryDeviceIdentity device,
            DateTimeOffset capturedAtUtc,
            double? valueOverride = null)
        {
            result.Add(new TelemetryReading(
                metricKey,
                valueOverride ?? reading.Value,
                unit,
                device,
                TelemetrySourceKind.HwInfo,
                CompositeSourceId(reading),
                reading.Label,
                capturedAtUtc));
        }

        private static string CompositeSourceId(HwInfoReadingEntry reading) =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{reading.SensorIndex}:{reading.ReadingId}");

        private static double ToBytes(double value, TelemetryUnit unit) =>
            unit == TelemetryUnit.Gigabyte
                ? TelemetryUnitConversion.GibibytesToBytes(value)
                : TelemetryUnitConversion.MegabytesToBytes(value);

        private static bool IsValidTemperature(double value) =>
            value > 0 && value <= MaxPlausibleTemperatureCelsius;

        private static bool LabelEquals(string label, string expected) =>
            string.Equals(label, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsFamily(string sensorName, string prefix) =>
            sensorName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>类型码优先（官方枚举），文本单位兜底——比纯文本更可靠（§40）。</summary>
        public static TelemetryUnit ResolveUnit(HwInfoReadingEntry reading)
        {
            switch (reading.ReadingType)
            {
                case 1: return TelemetryUnit.Celsius;
                case 5: return TelemetryUnit.Watt;
                case 6: return TelemetryUnit.Megahertz;
                case 7: return TelemetryUnit.Percent;
                default: return NormalizeUnitText(reading.Unit);
            }
        }

        /// <summary>官方共享内存的单位是文本；精确集合之外一律 None（保持 Raw）。</summary>
        public static TelemetryUnit NormalizeUnitText(string? unitText) =>
            unitText?.Trim() switch
            {
                "°C" or "C" => TelemetryUnit.Celsius,
                "W" => TelemetryUnit.Watt,
                "MHz" => TelemetryUnit.Megahertz,
                "%" => TelemetryUnit.Percent,
                "MB" => TelemetryUnit.Megabyte,
                "GB" => TelemetryUnit.Gigabyte,
                _ => TelemetryUnit.None
            };

        /// <summary>
        /// 按父传感器名分族给出源侧设备事实（含 ordinal 与名称），身份与事实同源。
        /// </summary>
        public static SourceDeviceInfo DescribeSourceDevice(
            IReadOnlyList<HwInfoSensorEntry> sensors,
            uint sensorIndex)
        {
            var sensor = sensors.FirstOrDefault(entry => entry.SensorIndex == sensorIndex);
            if (sensor is null)
            {
                return new SourceDeviceInfo(
                    TelemetrySourceKind.HwInfo,
                    TelemetryDeviceKind.System,
                    $"shm:{sensorIndex}",
                    string.Empty,
                    (int)sensorIndex,
                    []);
            }

            if (IsFamily(sensor.SensorName, "CPU"))
            {
                // HWiNFO 会为同一颗 CPU 暴露多个变体传感器（DTS/Enhanced/C-State…），
                // 它们描述同一物理单元——折叠为单一逻辑设备，避免 canonical 碎片化。
                return new SourceDeviceInfo(
                    TelemetrySourceKind.HwInfo, TelemetryDeviceKind.Cpu,
                    "cpu", sensor.SensorName, 0, []);
            }

            if (IsFamily(sensor.SensorName, "GPU"))
            {
                var ordinal = sensors
                    .Where(entry => IsFamily(entry.SensorName, "GPU"))
                    .Select(entry => entry.SensorIndex)
                    .Distinct()
                    .OrderBy(index => index)
                    .Select((index, position) => (index, position))
                    .FirstOrDefault(pair => pair.index == sensorIndex).position;
                return new SourceDeviceInfo(
                    TelemetrySourceKind.HwInfo, TelemetryDeviceKind.Gpu,
                    $"gpu:{sensorIndex}", sensor.SensorName, ordinal, []);
            }

            // V2-M4.5B：每模块传感器（RAM Module #N / DIMM N / DDR5 DIMM [#N] (…)）
            // 优先于泛 Memory 家族；DDR5 命名里的 DeviceLocator 是强身份证据。
            if (MemoryModuleSensorNames.TryGetModuleIndex(sensor.SensorName, out var moduleIndex))
            {
                var strongIds = MemoryModuleSensorNames.TryGetModuleDeviceLocator(
                    sensor.SensorName, out var locator)
                    ? ["locator:" + locator]
                    : Array.Empty<string>();
                return new SourceDeviceInfo(
                    TelemetrySourceKind.HwInfo, TelemetryDeviceKind.MemoryModule,
                    $"memory-module:{moduleIndex}", sensor.SensorName, moduleIndex, strongIds);
            }

            if (IsFamily(sensor.SensorName, "RAM") || IsFamily(sensor.SensorName, "Memory"))
            {
                return new SourceDeviceInfo(
                    TelemetrySourceKind.HwInfo, TelemetryDeviceKind.Memory,
                    "memory", sensor.SensorName, 0, []);
            }

            return new SourceDeviceInfo(
                TelemetrySourceKind.HwInfo, TelemetryDeviceKind.System,
                $"shm:{sensorIndex}", sensor.SensorName, (int)sensorIndex, []);
        }

        /// <summary>
        /// 按父传感器名分族给出设备身份；无法归族的传感器挂 system 设备（Raw 保真）。
        /// </summary>
        public static TelemetryDeviceIdentity ResolveDevice(
            IReadOnlyList<HwInfoSensorEntry> sensors,
            uint sensorIndex)
        {
            var info = DescribeSourceDevice(sensors, sensorIndex);
            return new TelemetryDeviceIdentity(info.Kind, info.NativeDeviceId, info.NativeDeviceName);
        }
    }
}
