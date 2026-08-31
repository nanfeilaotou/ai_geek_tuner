using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>
    /// V2-M3.3 §19：普通 Hardware 页面装配回归——
    /// 匿名 AIDA GPU 绝不出现；resolved GPU 各出现一次；
    /// 存储用真实型号；内存标签不出现 provider 名；核心趋势默认 6–8 行。
    /// </summary>
    public sealed class HardwarePageViewBuilderTests
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
            "NVIDIA GeForce RTX 4080 Laptop GPU");

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

        private static string? Label(TelemetryMetricKey metric) => metric.Value switch
        {
            "cpu.package.temperature" => "温度",
            "cpu.total.utilization" => "使用率",
            "cpu.clock" => "时钟频率",
            "cpu.package.power" => "Package Power",
            "gpu.core.temperature" => "温度",
            "gpu.core.utilization" => "使用率",
            "gpu.core.clock" => "核心频率",
            "gpu.board.power" => "功耗",
            "gpu.hotspot.temperature" => "热点温度",
            "gpu.memory.temperature" => "显存温度",
            "gpu.memory.used" => "已用显存",
            "memory.used" => "已用内存",
            "memory.utilization" => "使用率",
            "memory.clock" => "内存频率",
            "storage.temperature" => "温度",
            _ => null,
        };

        private static string Format(double value, TelemetryUnit unit) =>
            $"{value:0.#} {unit}";

        private static string SourceDisplay(TelemetrySourceKind source) => source.ToString();

        private static IReadOnlyList<HardwarePageDeviceCard> BuildCards(TelemetrySnapshot s) =>
            HardwarePageViewBuilder.BuildCards(s, Label, Format, SourceDisplay);

        private static IReadOnlyList<HardwarePageTrendRow> BuildTrends(
            TelemetrySnapshot s, TelemetryTrendBuffer buffer) =>
            HardwarePageViewBuilder.BuildTrends(s, buffer, Label, Format);

        [Fact]
        public void ResolvedGpus_AppearExactlyOnce_AnonymousHidden()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
                Reading(TelemetryMetricKey.GpuCoreUtilization, 30, Intel),
            ], [], []);

            var cards = BuildCards(snapshot);
            var gpuCards = cards.Where(c => c.Kind == TelemetryDeviceKind.Gpu).ToArray();

            Assert.Equal(2, gpuCards.Length);
            Assert.Equal("GPU · NVIDIA GeForce RTX 4080 Laptop GPU", gpuCards[0].Title);
            Assert.Equal("GPU · Intel UHD Graphics", gpuCards[1].Title);
            Assert.DoesNotContain(gpuCards, c => c.FullName.Contains("GPU #"));
            Assert.All(cards, c => Assert.DoesNotContain("GPU #1", c.Title));
        }

        [Fact]
        public void DuplicateMetric_FromMergedDevice_DisplaysOnce()
        {
            // Hub 已按 claim 选择后的快照同设备同指标只有一条；
            // 若测试快照手动给出重复，装配器也必须只显示一次。
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia,
                    TelemetrySourceKind.LibreHardwareMonitor),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 61, Nvidia,
                    TelemetrySourceKind.Aida64),
            ], [], []);

            var card = Assert.Single(BuildCards(snapshot));
            var row = Assert.Single(card.Rows);
            Assert.NotNull(row);
        }

        [Fact]
        public void CoreMetricCaps_GpuHotspotAndMemoryUsed_AreExcluded()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia),
                Reading(TelemetryMetricKey.GpuHotspotTemperature, 70, Nvidia),
                Reading(TelemetryMetricKey.GpuMemoryTemperature, 62, Nvidia),
                Reading(TelemetryMetricKey.GpuMemoryUsed, 8, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreUtilization, 40, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreClock, 2100, Nvidia),
                Reading(TelemetryMetricKey.GpuBoardPower, 65, Nvidia),
            ], [], []);

            var card = Assert.Single(BuildCards(snapshot));
            Assert.Equal(new[] { "温度", "使用率", "核心频率", "功耗" },
                card.Rows.Select(r => r.Label).ToArray());
        }

        [Fact]
        public void CpuThrottling_IsNotCoreMetric()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.CpuPackageTemperature, 80, Cpu),
                Reading(TelemetryMetricKey.CpuThrottling, 0, Cpu),
            ], [], []);

            var card = Assert.Single(BuildCards(snapshot));
            Assert.Equal(new[] { "温度" }, card.Rows.Select(r => r.Label).ToArray());
        }

        [Fact]
        public void Memory_TitleIsChineseLabel_NotProviderName()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.MemoryUsed, 17179869184, Memory),
                Reading(TelemetryMetricKey.MemoryUtilization, 45, Memory),
            ], [], []);

            var card = Assert.Single(BuildCards(snapshot));
            Assert.Equal("内存", card.Title);
            Assert.DoesNotContain(card.Rows, r => r.Label.Contains("Virtual"));
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

            var cards = BuildCards(snapshot);
            Assert.Equal(new[] { "磁盘 · Predator SSD GM7 M.2 4TB", "磁盘 · Samsung MZVL21T0HCLR-00B00" },
                cards.Select(c => c.Title).ToArray());
            Assert.DoesNotContain(cards, c => c.Title.Contains("HDD"));
        }

        [Fact]
        public void Storage_FallsBackToDiskNumber_WhenNoRealNameExists()
        {
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.StorageTemperature, 35, AidaHdd1, TelemetrySourceKind.Aida64),
            ], [], []);

            var card = Assert.Single(BuildCards(snapshot));
            Assert.Equal("磁盘 #1", card.Title);
        }

        [Fact]
        public void UnresolvedRawReadings_RetainedInSnapshot()
        {
            var aidaIdentity = new TelemetryDeviceIdentity(
                TelemetryDeviceKind.Gpu, "gpu:1", "GPU #1");
            var raw = new RawTelemetryReading(
                TelemetrySourceKind.Aida64, "THDD", "GPU #1 温度", 65,
                TelemetryUnit.Celsius, aidaIdentity,
                SourceDeviceInfo.Create(
                    TelemetrySourceKind.Aida64, TelemetryDeviceKind.Gpu,
                    "gpu:1", "GPU #1", 0),
                Now);
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
            ], [], [raw]);

            Assert.Empty(BuildCards(snapshot));   // 展示层隐藏
            Assert.Single(snapshot.RawReadings);  // Raw 层保留
        }

        // ---- §7/§8 趋势 ----

        [Fact]
        public void DefaultTrends_CoreSetOnly_ShortTitles()
        {
            var buffer = new TelemetryTrendBuffer();
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.CpuPackageTemperature, 80, Cpu),
                Reading(TelemetryMetricKey.CpuTotalUtilization, 30, Cpu),
                Reading(TelemetryMetricKey.CpuPackagePower, 45, Cpu),
                Reading(TelemetryMetricKey.CpuClock, 4000, Cpu),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreUtilization, 40, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreClock, 2100, Nvidia),
                Reading(TelemetryMetricKey.GpuBoardPower, 65, Nvidia),
                Reading(TelemetryMetricKey.MemoryUtilization, 45, Memory),
            ], [], []);
            buffer.AddSnapshot(snapshot);

            var trends = BuildTrends(snapshot, buffer);

            Assert.Equal(new[] { "CPU · 温度", "CPU · 使用率", "CPU · Package Power", "RTX 4080 · 温度", "RTX 4080 · 使用率", "RTX 4080 · 核心频率", "RTX 4080 · 功耗", "内存 · 使用率" }, trends.Select(t => t.Label).ToArray());
            Assert.Contains(trends, t => t.FullName == "NVIDIA GeForce RTX 4080 Laptop GPU");
            Assert.All(trends, t => Assert.DoesNotContain("GPU #", t.Label));
            Assert.Contains(trends, t => t.FullName == "AMD Ryzen 9 7945HX");
        }

        [Fact]
        public void Trends_ExcludeIGpu_StorageTemp_AndUnresolved()
        {
            var buffer = new TelemetryTrendBuffer();
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 55, Intel),
                Reading(TelemetryMetricKey.StorageTemperature, 41, Predator),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
            ], [], []);
            buffer.AddSnapshot(snapshot);

            Assert.Empty(BuildTrends(snapshot, buffer));
        }

        [Fact]
        public void FlatMetric_InCoreSet_StillShown_OutOfCoreSet_NeverAutoAdded()
        {
            // 平线（min==max）但属于核心集 → 保留；平线但不在核心集 → 绝不自动加入。
            var buffer = new TelemetryTrendBuffer();
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.CpuPackageTemperature, 80, Cpu),      // 核心集内
                Reading(TelemetryMetricKey.CpuClock, 4000, Cpu),                 // 不在趋势集
            ], [], []);
            buffer.AddSnapshot(snapshot);
            buffer.AddSnapshot(snapshot); // 两次同值 → 平线

            var trends = BuildTrends(snapshot, buffer);

            var trend = Assert.Single(trends);
            Assert.Equal("CPU · 温度", trend.Label);
            Assert.Equal(trend.Min, trend.Max);
        }

        [Fact]
        public void DuplicatePhysicalGpu_TrendAppearsOnce()
        {
            var buffer = new TelemetryTrendBuffer();
            var snapshot = new TelemetrySnapshot(Now,
            [
                Reading(TelemetryMetricKey.GpuCoreTemperature, 60, Nvidia),
                Reading(TelemetryMetricKey.GpuCoreTemperature, 65, AidaGpu1, TelemetrySourceKind.Aida64),
            ], [], []);
            buffer.AddSnapshot(snapshot);

            var trend = Assert.Single(BuildTrends(snapshot, buffer));
            Assert.StartsWith("RTX 4080", trend.Label);
        }
    }
}
