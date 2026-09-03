using System;
using System.Linq;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Presentation;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>V2-M4.5B Gate G/I：内存模块温度 presentation helper。</summary>
    public class MemoryTemperaturePresentationTests
    {
        private static readonly DateTimeOffset CapturedAtUtc =
            new(2024, 9, 1, 8, 0, 0, TimeSpan.Zero);

        private static TelemetryReading ModuleReading(
            string key, string name, double value, TelemetrySourceKind source) =>
            new(
                TelemetryMetricKey.MemoryModuleTemperature,
                value,
                TelemetryUnit.Celsius,
                TelemetryDeviceIdentity.MemoryModule(key, name),
                source,
                "raw:" + key,
                "SPD Hub Temperature",
                CapturedAtUtc);

        [Fact]
        public void Highest_IsMaxOfCurrentModuleTemperatures()
        {
            var snapshot = new TelemetrySnapshot(
                CapturedAtUtc,
                [
                    ModuleReading("memory-module:0", "RAM Module #0", 36.5, TelemetrySourceKind.HwInfo),
                    ModuleReading("memory-module:1", "RAM Module #1", 38.0, TelemetrySourceKind.HwInfo),
                ],
                [],
                []);

            Assert.Equal(38.0, MemoryTemperaturePresentation.CurrentHighestModuleTemperature(snapshot));
        }

        [Fact]
        public void Provenance_RetainedPerRow()
        {
            var snapshot = new TelemetrySnapshot(
                CapturedAtUtc,
                [ModuleReading("memory-module:0", "RAM Module #0", 36.5, TelemetrySourceKind.HwInfo)],
                [],
                []);

            var row = MemoryTemperaturePresentation.CollectCurrentRows(snapshot).Single();

            Assert.Equal("memory-module:0", row.ModuleKey);
            Assert.Equal("RAM Module #0", row.ModuleDisplayName);
            Assert.Equal(TelemetrySourceKind.HwInfo, row.Source);
            Assert.Equal("raw:memory-module:0", row.SourceMetricId);
            Assert.Equal("SPD Hub Temperature", row.SourceLabel);
        }

        [Fact]
        public void NoSensor_ReturnsNullAndEmpty()
        {
            var snapshot = TelemetrySnapshot.Empty(CapturedAtUtc);

            Assert.Empty(MemoryTemperaturePresentation.CollectCurrentRows(snapshot));
            Assert.Null(MemoryTemperaturePresentation.CurrentHighestModuleTemperature(snapshot));
        }

        // ---- V2-M4.5C Gate I：DIMM 标签解析 ----

        [Fact]
        public void DescribeWithLabels_LocatorStrongId_MapsToStaticDimmIndex()
        {
            var dimm0 = TelemetryDeviceIdentity.MemoryModule("memory-module:0", "HWiNFO DIMM #0");
            var dimm2 = TelemetryDeviceIdentity.MemoryModule("memory-module:2", "HWiNFO DIMM #2");
            var snapshot = new TelemetrySnapshot(
                CapturedAtUtc,
                [
                    ModuleReading("memory-module:0", "HWiNFO DIMM #0", 53.3, TelemetrySourceKind.HwInfo),
                    ModuleReading("memory-module:2", "HWiNFO DIMM #2", 53.0, TelemetrySourceKind.HwInfo),
                ],
                [],
                [
                    new RawTelemetryReading(
                        TelemetrySourceKind.HwInfo, "7:16777216", "SPD Hub Temperature", 53.3,
                        TelemetryUnit.Celsius, dimm0,
                        new SourceDeviceInfo(TelemetrySourceKind.HwInfo, TelemetryDeviceKind.MemoryModule,
                            "memory-module:0", "DDR5 DIMM [#0]", 0,
                            ["locator:Controller0-ChannelA-DIMM0"]), CapturedAtUtc),
                    new RawTelemetryReading(
                        TelemetrySourceKind.HwInfo, "8:16777216", "SPD Hub Temperature", 53.0,
                        TelemetryUnit.Celsius, dimm2,
                        new SourceDeviceInfo(TelemetrySourceKind.HwInfo, TelemetryDeviceKind.MemoryModule,
                            "memory-module:2", "DDR5 DIMM [#2]", 2,
                            ["locator:Controller1-ChannelA-DIMM0"]), CapturedAtUtc),
                ]);
            var staticModules = new[]
            {
                new AIGeekTuner.Models.Hardware.Inventory.MemoryModuleInfo(
                    "Controller0-ChannelA-DIMM0", null, null, null, null, null, null, null, null, null, null,
                    AIGeekTuner.Models.Hardware.Inventory.InventorySource.Wmi),
                new AIGeekTuner.Models.Hardware.Inventory.MemoryModuleInfo(
                    "Controller1-ChannelA-DIMM0", null, null, null, null, null, null, null, null, null, null,
                    AIGeekTuner.Models.Hardware.Inventory.InventorySource.Wmi),
            };

            var labels = MemoryTemperaturePresentation.DescribeWithLabels(snapshot, staticModules);

            Assert.Equal(
                [("memory-module:0", "DIMM 1", 53.3), ("memory-module:2", "DIMM 2", 53.0)],
                labels.Select(l => (l.ModuleKey, l.Label, l.ValueCelsius)).ToArray());
        }

        [Fact]
        public void DescribeWithLabels_NoLocator_UsesSafeFallbackNames()
        {
            var snapshot = new TelemetrySnapshot(
                CapturedAtUtc,
                [
                    ModuleReading("src:Aida64:memory-module:1", "DIMM #1", 41.0, TelemetrySourceKind.Aida64),
                    ModuleReading("src:Aida64:memory-module:2", "DIMM #2", 42.0, TelemetrySourceKind.Aida64),
                ],
                [],
                []);

            var labels = MemoryTemperaturePresentation.DescribeWithLabels(snapshot, null);

            Assert.Equal(["内存模块 A", "内存模块 B"], labels.Select(l => l.Label).ToArray());
        }
    }
}