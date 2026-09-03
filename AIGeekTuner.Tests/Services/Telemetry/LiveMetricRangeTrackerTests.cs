using System;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Presentation;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>V2-M4.5C Gate D/K：来源无关的实时范围跟踪。</summary>
    public class LiveMetricRangeTrackerTests
    {
        private static readonly DateTimeOffset Now = new(2024, 9, 1, 8, 0, 0, TimeSpan.Zero);

        private static TelemetryReading Reading(
            TelemetryMetricKey metric, double value, TelemetryDeviceIdentity? device = null) =>
            new(metric, value, TelemetryUnit.Celsius,
                device ?? TelemetryDeviceIdentity.Cpu("CPU"),
                TelemetrySourceKind.HwInfo, "raw", null, Now);

        private static TelemetrySnapshot Snapshot(params TelemetryReading[] readings) =>
            new(Now, readings, [], []);

        [Fact]
        public void Initial_HasNoRange()
        {
            var tracker = new LiveMetricRangeTracker();

            Assert.Null(tracker.GetRange("cpu", TelemetryMetricKey.CpuPackageTemperature));
        }

        [Fact]
        public void Update_TracksCurrentLowHigh()
        {
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(Snapshot(Reading(TelemetryMetricKey.CpuPackageTemperature, 70)));
            tracker.Update(Snapshot(Reading(TelemetryMetricKey.CpuPackageTemperature, 85)));
            tracker.Update(Snapshot(Reading(TelemetryMetricKey.CpuPackageTemperature, 62)));

            var range = tracker.GetRange("cpu", TelemetryMetricKey.CpuPackageTemperature);
            Assert.NotNull(range);
            Assert.Equal(62, range!.Current);
            Assert.Equal(62, range.Low);
            Assert.Equal(85, range.High);
        }

        [Fact]
        public void Reset_ClearsRanges_NextSnapshotStartsFresh()
        {
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(Snapshot(Reading(TelemetryMetricKey.CpuPackageTemperature, 90)));

            tracker.Reset();

            Assert.Null(tracker.GetRange("cpu", TelemetryMetricKey.CpuPackageTemperature));

            tracker.Update(Snapshot(Reading(TelemetryMetricKey.CpuPackageTemperature, 55)));
            var range = tracker.GetRange("cpu", TelemetryMetricKey.CpuPackageTemperature);
            Assert.Equal(55, range!.Low);
            Assert.Equal(55, range.High);
        }

        [Fact]
        public void SourceFallback_Continuity_SameDevice()
        {
            // Hub fallback 后（HWiNFO → AIDA64），AIGeekTuner 范围按 canonical
            // (DeviceKey, MetricKey) 连续累计，与来源无关。
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(Snapshot(Reading(TelemetryMetricKey.CpuPackageTemperature, 70)));
            tracker.Update(Snapshot(
                new TelemetryReading(
                    TelemetryMetricKey.CpuPackageTemperature, 75, TelemetryUnit.Celsius,
                    TelemetryDeviceIdentity.Cpu("CPU"),
                    TelemetrySourceKind.Aida64, "raw2", null, Now)));

            var range = tracker.GetRange("cpu", TelemetryMetricKey.CpuPackageTemperature);
            Assert.Equal(70, range!.Low);
            Assert.Equal(75, range.High);
            Assert.Equal(75, range.Current);
        }

        [Fact]
        public void MultipleGpus_AreIsolated()
        {
            var nvidia = TelemetryDeviceIdentity.GpuByIndex(0, "NVIDIA RTX 4080");
            var intel = TelemetryDeviceIdentity.GpuByIndex(1, "Intel UHD");
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(Snapshot(
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, nvidia),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 52, intel)));

            Assert.Equal(65, tracker.GetRange(nvidia.DeviceKey, TelemetryMetricKey.GpuCoreTemperature)!.Current);
            Assert.Equal(52, tracker.GetRange(intel.DeviceKey, TelemetryMetricKey.GpuCoreTemperature)!.Current);
        }

        [Fact]
        public void MultipleDisks_AreIsolated()
        {
            var diskA = TelemetryDeviceIdentity.Storage("predator", "Predator SSD");
            var diskB = TelemetryDeviceIdentity.Storage("samsung", "Samsung NVMe");
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(Snapshot(
                Reading(TelemetryMetricKey.StorageTemperature, 53, diskA),
                Reading(TelemetryMetricKey.StorageTemperature, 41, diskB)));

            Assert.Equal(53, tracker.GetRange(diskA.DeviceKey, TelemetryMetricKey.StorageTemperature)!.Current);
            Assert.Equal(41, tracker.GetRange(diskB.DeviceKey, TelemetryMetricKey.StorageTemperature)!.Current);
        }

        [Fact]
        public void HighestAcrossDevices_ReturnsMax_AndMissingReturnsNull()
        {
            var dimm0 = TelemetryDeviceIdentity.MemoryModule("memory-module:0", "DIMM 0");
            var dimm1 = TelemetryDeviceIdentity.MemoryModule("memory-module:1", "DIMM 1");
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(Snapshot(
                Reading(TelemetryMetricKey.MemoryModuleTemperature, 53.3, dimm0),
                Reading(TelemetryMetricKey.MemoryModuleTemperature, 53.0, dimm1)));

            var highest = tracker.GetHighestAcrossDevices(
                Snapshot(
                    Reading(TelemetryMetricKey.MemoryModuleTemperature, 53.3, dimm0),
                    Reading(TelemetryMetricKey.MemoryModuleTemperature, 53.0, dimm1)),
                TelemetryMetricKey.MemoryModuleTemperature);
            Assert.NotNull(highest);
            Assert.Equal(53.3, highest!.High);

            var empty = tracker.GetHighestAcrossDevices(
                Snapshot(Reading(TelemetryMetricKey.MemoryUtilization, 40)), TelemetryMetricKey.MemoryModuleTemperature);
            Assert.Null(empty);
        }
    }
}
