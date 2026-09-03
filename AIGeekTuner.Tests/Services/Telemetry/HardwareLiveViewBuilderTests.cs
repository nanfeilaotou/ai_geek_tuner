using System;
using System.Linq;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Presentation;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>
    /// V2-M4.5C.1：实时设备卡装配回归（原 HardwarePageViewBuilderTests 策略锁
    /// 随 Gate B 旧卡列表删除迁入此处）——
    /// 匿名 AIDA GPU 绝不出现；resolved GPU 各出现一次且标题不带 source-local
    /// 前缀；存储用真实型号，匿名盘回退“磁盘 #N”，绝不写 HDD；
    /// 内存标题固定中文；同设备同指标只显示一次。
    /// </summary>
    public sealed class HardwareLiveViewBuilderTests
    {
        private static TelemetryReading Reading(
            TelemetryMetricKey metric, double value, TelemetryDeviceIdentity device,
            TelemetrySourceKind source = TelemetrySourceKind.LibreHardwareMonitor) =>
            new(metric, value, UnitOf(metric), device, source, "raw:" + metric.Value, null, Now);

        private static TelemetryUnit UnitOf(TelemetryMetricKey metric) => metric.Value switch
        {
            "cpu.total.utilization" or "gpu.core.utilization" or "memory.utilization" => TelemetryUnit.Percent,
            "cpu.clock" or "gpu.core.clock" or "memory.clock" => TelemetryUnit.Megahertz,
            "cpu.package.power" or "gpu.board.power" => TelemetryUnit.Watt,
            "memory.used" => TelemetryUnit.Byte,
            _ => TelemetryUnit.Celsius,
        };

        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        private static readonly TelemetryDeviceIdentity Nvidia = new(
            TelemetryDeviceKind.Gpu, "gpu:name:nvidia-geforce-rtx-4080-laptop-gpu",
            "GPU [#1]: NVIDIA GeForce RTX 4080 Laptop GPU");

        private static readonly TelemetryDeviceIdentity AidaGpu1 = new(
            TelemetryDeviceKind.Gpu, "gpu:1", "GPU #1");

        private static readonly TelemetryDeviceIdentity Intel = new(
            TelemetryDeviceKind.Gpu, "gpu:name:intel-uhd-graphics", "Intel UHD Graphics");

        private static readonly TelemetryDeviceIdentity Cpu = new(
            TelemetryDeviceKind.Cpu, "cpu:singleton", "AMD Ryzen 9 7945HX");

        private static readonly TelemetryDeviceIdentity Memory = new(
            TelemetryDeviceKind.Memory, "memory:singleton", "Virtual Memory");

        private static readonly TelemetryDeviceIdentity Predator = new(
            TelemetryDeviceKind.Storage, "storage:nvme0", "Predator SSD GM7 M.2 4TB");

        private static readonly TelemetryDeviceIdentity Samsung = new(
            TelemetryDeviceKind.Storage, "storage:nvme1", "Samsung MZVL21T0HCLR-00B00");

        private static readonly TelemetryDeviceIdentity AidaHdd1 = new(
            TelemetryDeviceKind.Storage, "storage:hdd:0", "HDD #1");

        private static IReadOnlyList<LiveDeviceCard> Build(TelemetrySnapshot snapshot)
        {
            var tracker = new LiveMetricRangeTracker();
            tracker.Update(snapshot);
            return HardwareLiveViewBuilder.Build(snapshot, tracker);
        }

        [Fact]
        public void ResolvedGpus_AppearExactlyOnce_TitlesWithoutSourceLocalPrefix()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
                Reading(TelemetryMetricKey.GpuCoreUtilization, 30, Intel),
            ], [], []);

            var cards = Build(snapshot);
            var gpuCards = cards.Where(c => c.Title.StartsWith("GPU · ", StringComparison.Ordinal)).ToArray();

            Assert.Equal(2, gpuCards.Length);
            Assert.Equal("GPU · NVIDIA GeForce RTX 4080 Laptop GPU", gpuCards[0].Title);
            Assert.Equal("GPU · Intel UHD Graphics", gpuCards[1].Title);
            Assert.All(cards.Select(c => c.Title), t => Assert.DoesNotContain("GPU #1", t));
            Assert.All(cards.Select(c => c.Title), t => Assert.DoesNotContain("[#", t));
        }

        [Fact]
        public void AnonymousGpu_WhenNoResolvedName_IsHidden()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
            ], [], []);

            var cards = Build(snapshot);
            Assert.DoesNotContain(cards, c => c.Title.Contains("GPU ·"));
        }

        [Fact]
        public void DuplicateMetric_FromMergedDevice_DisplaysOnce()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia,
                    TelemetrySourceKind.LibreHardwareMonitor),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 61, Nvidia,
                    TelemetrySourceKind.Aida64),
            ], [], []);

            var gpuCards = Build(snapshot)
                .Where(c => c.Title.StartsWith("GPU · ", StringComparison.Ordinal)).ToArray();
            var card = Assert.Single(gpuCards);
            var meter = Assert.Single(card.Meters);
            Assert.Equal("核心温度", meter.Label);
        }

        [Fact]
        public void CpuCard_TitleIsShortName_MetersAndNumericsOrdered()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.CpuPackageTemperature, 80, Cpu),
                Reading(TelemetryMetricKey.CpuTotalUtilization, 15, Cpu),
                Reading(TelemetryMetricKey.CpuClock, 2400, Cpu),
                Reading(TelemetryMetricKey.CpuPackagePower, 30, Cpu),
            ], [], []);

            var card = Assert.Single(Build(snapshot), c => c.Title == "CPU");
            Assert.Equal(new[] { "温度", "使用率" }, card.Meters.Select(m => m.Label).ToArray());
            Assert.Equal(new[] { "核心频率", "Package Power" }, card.Numerics.Select(n => n.Label).ToArray());
        }

        [Fact]
        public void Memory_TitleIsChineseLabel_NotProviderName()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.MemoryUsed, 17179869184, Memory),
                Reading(TelemetryMetricKey.MemoryUtilization, 45, Memory),
            ], [], []);

            var card = Assert.Single(Build(snapshot), c => c.Title == "内存");
            Assert.DoesNotContain(card.Numerics, r => r.Label.Contains("Virtual", StringComparison.Ordinal));
        }

        [Fact]
        public void Storage_UsesRealModelNames_AndHidesAnonymousWhenRealExists()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.StorageTemperature, 41, Predator),
                Reading(TelemetryMetricKey.StorageTemperature, 38, Samsung),
                Reading(TelemetryMetricKey.StorageTemperature, 35, AidaHdd1, TelemetrySourceKind.Aida64),
            ], [], []);

            var cards = Build(snapshot);
            Assert.Equal(
                new[] { "磁盘 · Predator SSD GM7 M.2 4TB", "磁盘 · Samsung MZVL21T0HCLR-00B00" },
                cards.Select(c => c.Title).ToArray());
            Assert.DoesNotContain(cards, c => c.Title.Contains("HDD", StringComparison.Ordinal));
        }

        [Fact]
        public void Storage_FallsBackToDiskNumber_NeverHdd_WhenNoRealNameExists()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.StorageTemperature, 35, AidaHdd1, TelemetrySourceKind.Aida64),
            ], [], []);

            var card = Assert.Single(Build(snapshot));
            Assert.Equal("磁盘 #1", card.Title);
        }

        [Fact]
        public void UnresolvedRawReadings_RetainedInSnapshot()
        {
            var raw = new RawTelemetryReading(
                TelemetrySourceKind.Aida64, "THDD", "GPU #1 温度", 65,
                TelemetryUnit.Celsius, AidaGpu1,
                SourceDeviceInfo.Create(
                    TelemetrySourceKind.Aida64, TelemetryDeviceKind.Gpu,
                    "gpu:1", "GPU #1", 0),
                Now);
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
            ], [], [raw]);

            Assert.Empty(Build(snapshot));        // 展示层隐藏
            Assert.Single(snapshot.RawReadings);  // Raw 层保留
        }
    }
}
