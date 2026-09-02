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
    }
}
