using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.HwInfo;

namespace AIGeekTuner.Tests.Services.Telemetry.HwInfo
{
    public class HwInfoCanonicalMapperTests
    {
        private static readonly DateTimeOffset CapturedAtUtc =
            new(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);

        private static readonly HwInfoSensorEntry[] Sensors =
        [
            new HwInfoSensorEntry(0, "CPU [#0]: Intel Core"),
            new HwInfoSensorEntry(1, "GPU [#1]: NVIDIA RTX 4080"),
            new HwInfoSensorEntry(2, "GPU [#2]: AMD Radeon 780M"),
            new HwInfoSensorEntry(3, "RAM [#0]: Memory Controller"),
            new HwInfoSensorEntry(4, "Motherboard [#0]: ASUS ROG"),
        ];

        [Fact]
        public void CpuPackage_MapsTemperaturePowerUtilization()
        {
            var readings = new[]
            {
                new HwInfoReadingEntry(0, 10, "CPU Package", "°C", 71.4),
                new HwInfoReadingEntry(0, 11, "CPU Package Power", "W", 84),
                new HwInfoReadingEntry(0, 12, "CPU Total", "%", 61),
            };

            var canonical = HwInfoCanonicalMapper.Map(Sensors, readings, CapturedAtUtc);

            Assert.Equal(3, canonical.Count);
            Assert.Contains(canonical, r =>
                r.MetricKey == TelemetryMetricKey.CpuPackageTemperature && r.Value == 71.4);
            Assert.All(canonical, r => Assert.Equal("cpu", r.Device.DeviceKey));
        }

        [Fact]
        public void TwoGpus_GetDistinctOrdinals_AndHotspotMaps()
        {
            var readings = new[]
            {
                new HwInfoReadingEntry(1, 20, "GPU Hot Spot", "°C", 76),
                new HwInfoReadingEntry(2, 21, "GPU Hot Spot", "°C", 62),
            };

            var canonical = HwInfoCanonicalMapper.Map(Sensors, readings, CapturedAtUtc);

            var byKey = canonical.ToDictionary(r => r.Device.DeviceKey, r => r.Value);
            Assert.Equal(2, byKey.Count);
            Assert.Equal(76, byKey["gpu:0"]);
            Assert.Equal(62, byKey["gpu:1"]);
        }

        [Fact]
        public void GpuMemoryUsed_GigabytesConvertToBytes()
        {
            var readings = new[] { new HwInfoReadingEntry(1, 30, "GPU Memory Used", "GB", 3.5) };

            var canonical = HwInfoCanonicalMapper.Map(Sensors, readings, CapturedAtUtc);

            var reading = Assert.Single(canonical);
            Assert.Equal(TelemetryMetricKey.GpuMemoryUsed, reading.MetricKey);
            Assert.Equal(TelemetryUnit.Byte, reading.Unit);
            Assert.Equal(3.5 * 1073741824d, reading.Value);
        }

        [Fact]
        public void CompositeSourceId_PairsParentSensorWithReadingId()
        {
            var readings = new[]
            {
                new HwInfoReadingEntry(1, 40, "GPU Core", "°C", 60),
                new HwInfoReadingEntry(2, 40, "GPU Core", "°C", 50),
            };

            var canonical = HwInfoCanonicalMapper.Map(Sensors, readings, CapturedAtUtc);

            // readingId=40 在不同父传感器下重复出现 —— 复合键必须避免冲突。
            var ids = canonical.Select(r => r.SourceMetricId).ToArray();
            Assert.Equal(2, ids.Distinct().Count());
        }

        [Fact]
        public void UnknownLabelsOrUnits_StayRawOnly()
        {
            var readings = new[]
            {
                new HwInfoReadingEntry(4, 50, "CPU Package", "°F", 160), // 单位不在精确集合
                new HwInfoReadingEntry(4, 51, "CPU (Package)", "°C", 70), // label 非精确匹配
                new HwInfoReadingEntry(99, 52, "GPU Core", "°C", 60), // 孤儿读数：无父传感器
            };

            var canonical = HwInfoCanonicalMapper.Map(Sensors, readings, CapturedAtUtc);

            Assert.Empty(canonical);
        }

        [Fact]
        public void UnitTextNormalization_ExactSets()
        {
            Assert.Equal(TelemetryUnit.Celsius, HwInfoCanonicalMapper.NormalizeUnitText(" °C "));
            Assert.Equal(TelemetryUnit.Watt, HwInfoCanonicalMapper.NormalizeUnitText("W"));
            Assert.Equal(TelemetryUnit.Percent, HwInfoCanonicalMapper.NormalizeUnitText("%"));
            Assert.Equal(TelemetryUnit.None, HwInfoCanonicalMapper.NormalizeUnitText("RPM"));
            Assert.Equal(TelemetryUnit.None, HwInfoCanonicalMapper.NormalizeUnitText(null));
        }
    }
}
