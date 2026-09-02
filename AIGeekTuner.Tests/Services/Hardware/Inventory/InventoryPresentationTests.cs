using System;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory.Presentation;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5B Gate A/B/I：Dashboard 与 Hardware 详情静态展示装配。
    /// 全部合成数据，不依赖真实机器。
    /// </summary>
    public class InventoryPresentationTests
    {
        private static readonly DateTimeOffset NowUtc =
            new(2024, 9, 1, 8, 0, 0, TimeSpan.Zero);

        private static HardwareInventorySnapshot Snapshot(
            CpuInventoryInfo? cpu = null,
            MotherboardInventoryInfo? motherboard = null,
            BiosInventoryInfo? bios = null,
            OsInventoryInfo? os = null,
            MemoryModuleInfo[]? memory = null,
            GpuInventoryInfo[]? gpus = null,
            StorageDiskInventoryInfo[]? disks = null,
            MonitorInventoryInfo[]? monitors = null,
            AudioDeviceInfo[]? audio = null,
            NetworkAdapterInventoryInfo[]? network = null,
            AudioControllerInfo[]? audioControllers = null) =>
            new(
                cpu, motherboard, bios, os,
                memory ?? [],
                gpus ?? [],
                disks ?? [],
                monitors ?? [],
                audio ?? [],
                network ?? [],
                NowUtc,
                audioControllers);

        // ---------------------------------------------------------- fixtures
        private static CpuInventoryInfo Cpu() => new(
            "13th Gen Intel(R) Core(TM) i9-13980HX", "GenuineIntel", "x64",
            24, 32, 5800, 2200, 20480, 36864, true, InventorySource.Wmi);

        private static MotherboardInventoryInfo Board() => new(
            "ASUSTeK COMPUTER INC.", "G834JZ", "1.0", "ABCDEF0123", InventorySource.Wmi);

        private static BiosInventoryInfo Bios() => new(
            "American Megatrends International, LLC.", "G834JZ.331",
            new DateTimeOffset(2024, 11, 11, 0, 0, 0, TimeSpan.Zero), "3.3", InventorySource.Wmi);

        private static MemoryModuleInfo Dimm(string locator, string part, ulong gb) => new(
            locator, "ChannelA", gb * 1024L * 1024L * 1024L, "SK Hynix", part,
            "12345678", 5600, 5600, "SODIMM", 64, 64, InventorySource.Wmi);

        private static GpuInventoryInfo Gpu(string name, ulong? vram = null) => new(
            name, "NVIDIA", 0x10DE, 0x24A0, null, "546.33",
            new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero),
            vram, 8_589_934_592, InventorySource.DXGI);

        private static MonitorInventoryInfo Monitor(
            string name, string? resolution = "2560 × 1600", double? hz = 240,
            double? diagonal = 17.7, bool? primary = true) => new(
            name, "BOE", "0B35", "SERIAL123", 2022, 38, 30, diagonal,
            resolution, hz, primary, name, InventorySource.WmiMonitor);

        private static StorageDiskInventoryInfo Disk(string model) => new(
            model, model, null, "F1.0", 4_000_786_130_944, "NVMe", "SSD", null,
            0, [new StoragePartitionInfo("C:", "NTFS", "System", 2_000_000_000_000, 500_000_000_000)],
            InventorySource.Wmi);

        private static AudioDeviceInfo Endpoint(
            string name, AudioEndpointDirection direction, bool isDefault = false) => new(
            name, "id", direction, "Active", isDefault, InventorySource.CoreAudio);

        private static NetworkAdapterInventoryInfo Adapter(
            string name, bool isVirtual) => new(
            name, name + " 描述", "Ethernet", "Up", 1_000_000_000, "AA-BB",
            ["192.168.1.10"], [], true, [], [], isVirtual, InventorySource.WindowsNetwork);

        // ------------------------------------------------- Dashboard (Gate A)
        [Fact]
        public void Dashboard_SixMandatoryRows_FixedOrder()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                cpu: Cpu(), motherboard: Board(), memory: [Dimm("DIMM0", "HMCG78MEBSA095N", 16)],
                gpus: [Gpu("NVIDIA GeForce RTX 4080 Laptop GPU", 12_579_766_272)],
                monitors: [Monitor("NE180QDM-NZ2")],
                disks: [Disk("Predator SSD GM7 M.2 4TB")]));

            Assert.Equal(
                ["主板", "处理器", "内存", "显卡", "显示器", "硬盘"],
                rows.Select(row => row.Label).ToArray());
        }

        [Fact]
        public void Dashboard_AudioRow_Conditional()
        {
            var withoutAudio = DashboardInventoryPresenter.BuildRows(Snapshot());
            Assert.DoesNotContain(withoutAudio, row => row.Label == "声卡");

            var withEndpoints = DashboardInventoryPresenter.BuildRows(Snapshot(
                audio: [Endpoint("扬声器 (Realtek)", AudioEndpointDirection.Playback)]));
            var audioRow = withEndpoints.Single(row => row.Label == "声卡");
            Assert.Contains("扬声器", audioRow.Value);

            var withControllers = DashboardInventoryPresenter.BuildRows(Snapshot(
                audioControllers: [new AudioControllerInfo(
                    "NVIDIA High Definition Audio", "NVIDIA", "OK", null, InventorySource.Wmi)]));
            var controllerRow = withControllers.Single(row => row.Label == "声卡");
            Assert.Contains("NVIDIA High Definition Audio", controllerRow.Value);
        }

        [Fact]
        public void Dashboard_NetworkRow_Conditional_PhysicalOnly()
        {
            var virtualOnly = DashboardInventoryPresenter.BuildRows(Snapshot(
                network: [Adapter("VMware Network Adapter VMnet1", isVirtual: true)]));
            Assert.DoesNotContain(virtualOnly, row => row.Label == "网卡");

            var withPhysical = DashboardInventoryPresenter.BuildRows(Snapshot(
                network:
                [
                    Adapter("Wi-Fi", isVirtual: false),
                    Adapter("VMware Network Adapter VMnet1", isVirtual: true),
                ]));
            var networkRow = withPhysical.Single(row => row.Label == "网卡");
            Assert.Contains("Wi-Fi", networkRow.Value);
            Assert.DoesNotContain("VMware", networkRow.Value);
        }

        [Fact]
        public void Dashboard_GpuRow_HidesBasicRenderDriver_AndReportsLargeVram()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                gpus:
                [
                    Gpu("NVIDIA GeForce RTX 4080 Laptop GPU", 12_579_766_272),
                    Gpu("Intel(R) UHD Graphics", 134_217_728),
                    Gpu("Microsoft Basic Render Driver"),
                ]));

            var gpuRow = rows.Single(row => row.Label == "显卡");
            Assert.Contains("RTX 4080 Laptop GPU (11.7 GB)", gpuRow.Value);
            Assert.Contains("Intel(R) UHD Graphics", gpuRow.Value);
            Assert.DoesNotContain("Basic Render", gpuRow.Value);
        }

        [Fact]
        public void Dashboard_MonitorRow_PrimarySummary_WithExtraCount()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                monitors:
                [
                    Monitor("NE180QDM-NZ2", primary: true),
                    Monitor("AOC U27U2", primary: false),
                ]));

            var monitorRow = rows.Single(row => row.Label == "显示器");
            Assert.Contains("NE180QDM-NZ2", monitorRow.Value);
            Assert.Contains("2560 × 1600", monitorRow.Value);
            Assert.Contains("240 Hz", monitorRow.Value);
            Assert.Contains("主屏", monitorRow.Value);
            Assert.Contains("另有 1 台显示器", monitorRow.Value);
        }

        [Fact]
        public void Dashboard_MonitorRow_MissingFields_Graceful()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                monitors: [Monitor("仅名称显示器", resolution: null, hz: null, diagonal: null, primary: null)]));

            var monitorRow = rows.Single(row => row.Label == "显示器");
            Assert.Contains("仅名称显示器", monitorRow.Value);
        }

        [Fact]
        public void Dashboard_MemoryRow_SummarizesTwoDimms()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                memory:
                [
                    Dimm("Controller0-ChannelA-DIMM0", "HMCG78MEBSA095N", 16),
                    Dimm("Controller1-ChannelA-DIMM0", "HMCG78MEBSA095N", 16),
                ]));

            var memoryRow = rows.Single(row => row.Label == "内存");
            Assert.Contains("32 GB", memoryRow.Value);
            Assert.Contains("SK Hynix", memoryRow.Value);
            Assert.Contains("5600 MHz", memoryRow.Value);
            Assert.Contains("2 条", memoryRow.Value);
        }

        // ------------------------------------------------- Detail (Gate B)
        [Fact]
        public void Detail_TwoDimms_DisplayedIndependently()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                memory:
                [
                    Dimm("Controller0-ChannelA-DIMM0", "PART-A", 16),
                    Dimm("Controller1-ChannelA-DIMM0", "PART-B", 16),
                ]));

            var memory = sections.Single(section => section.Title == "内存");
            var titles = memory.Cards.Select(card => card.Title).ToArray();
            Assert.Contains("总览", titles);
            Assert.Contains("Controller0-ChannelA-DIMM0", titles);
            Assert.Contains("Controller1-ChannelA-DIMM0", titles);
            var partA = memory.Cards.Single(card => card.Title == "Controller0-ChannelA-DIMM0");
            Assert.Contains(partA.Rows, row => row.Label == "Part Number" && row.Value == "PART-A");
            var partB = memory.Cards.Single(card => card.Title == "Controller1-ChannelA-DIMM0");
            Assert.Contains(partB.Rows, row => row.Label == "Part Number" && row.Value == "PART-B");
        }

        [Fact]
        public void Detail_OneMonitor_SingleCard_WithFields()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                monitors: [Monitor("NE180QDM-NZ2")]));

            var monitors = sections.Single(section => section.Title == "显示器");
            _ = monitors; // 断言见下
            var card = Assert.Single(monitors.Cards);
            Assert.Equal("NE180QDM-NZ2", card.Title);
            Assert.Contains(card.Rows, row => row.Label == "当前分辨率" && row.Value == "2560 × 1600");
            Assert.Contains(card.Rows, row => row.Label == "刷新率" && row.Value.Contains("240"));
            Assert.Contains(card.Rows, row => row.Label == "物理尺寸" && row.Value.Contains("17.7"));
            Assert.Contains(card.Rows, row => row.Label == "主屏" && row.Value == "是");
            Assert.Contains(card.Rows, row => row.Label == "生产年份" && row.Value == "2022");
        }

        [Fact]
        public void Detail_TwoMonitors_TwoCards()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                monitors:
                [
                    Monitor("NE180QDM-NZ2", primary: true),
                    Monitor("AOC U27U2", resolution: "3840 × 2160", hz: 60, diagonal: 27, primary: false),
                ]));

            var monitors = sections.Single(section => section.Title == "显示器");
            Assert.Equal(2, monitors.Cards.Count);
        }

        [Fact]
        public void Detail_MonitorMissingFields_NoNoiseRows()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                monitors:
                [
                    new MonitorInventoryInfo(
                        "无名显示器", null, null, null, null, null, null, null,
                        null, null, null, null, InventorySource.WmiMonitor),
                ]));

            var monitors = sections.Single(section => section.Title == "显示器");
            var card = Assert.Single(monitors.Cards);
            Assert.Equal("无名显示器", card.Title);
            Assert.Empty(card.Rows);
        }

        [Fact]
        public void Detail_GpuCards_IgpuVramHidden_BasicRenderHidden()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                gpus:
                [
                    Gpu("NVIDIA GeForce RTX 4080 Laptop GPU", 12_579_766_272),
                    Gpu("Intel(R) UHD Graphics", 134_217_728),
                    Gpu("Microsoft Basic Render Driver"),
                ]));

            var gpu = sections.Single(section => section.Title == "显卡");
            Assert.Equal(2, gpu.Cards.Count);
            var nvidia = gpu.Cards.Single(card => card.Title == "NVIDIA GeForce RTX 4080 Laptop GPU");
            Assert.Contains(nvidia.Rows, row => row.Label == "专用显存");
            var intel = gpu.Cards.Single(card => card.Title == "Intel(R) UHD Graphics");
            Assert.DoesNotContain(intel.Rows, row => row.Label == "专用显存");
        }

        [Fact]
        public void Detail_BasicRenderDriver_KeptOnlyAsFallback()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                gpus: [Gpu("Microsoft Basic Render Driver")]));

            var gpu = sections.Single(section => section.Title == "显卡");
            var card = Assert.Single(gpu.Cards);
            Assert.Equal("Microsoft Basic Render Driver", card.Title);
        }

        [Fact]
        public void Detail_AudioSection_SeparatesPlaybackCapture_AndMarksDefault()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                audio:
                [
                    Endpoint("扬声器 (Realtek)", AudioEndpointDirection.Playback, isDefault: true),
                    Endpoint("麦克风阵列", AudioEndpointDirection.Capture),
                ],
                audioControllers:
                [
                    new AudioControllerInfo(
                        "Realtek Audio", "Realtek", "OK", null, InventorySource.Wmi),
                ]));

            var audio = sections.Single(section => section.Title == "声卡");
            var titles = audio.Cards.Select(card => card.Title).ToArray();
            Assert.Contains("音频控制器", titles);
            Assert.Contains("播放设备", titles);
            Assert.Contains("录制设备", titles);
            var playback = audio.Cards.Single(card => card.Title == "播放设备");
            Assert.Contains(playback.Rows, row => row.Value.Contains("默认"));
        }

        [Fact]
        public void Detail_NetworkSection_RetainsVirtualAdapters_WithMark()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                network:
                [
                    Adapter("Wi-Fi", isVirtual: false),
                    Adapter("VMware Network Adapter VMnet1", isVirtual: true),
                ]));

            var network = sections.Single(section => section.Title == "网卡");
            Assert.Equal(2, network.Cards.Count);
            Assert.Contains(network.Cards, card => card.Title.Contains("VMware") && card.Title.Contains("虚拟"));
            // 物理适配器排在虚拟前面
            Assert.Equal("Wi-Fi", network.Cards[0].Title);
        }

        [Fact]
        public void Detail_StorageSection_ShowsVolumes()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                disks: [Disk("Predator SSD GM7 M.2 4TB")]));

            var storage = sections.Single(section => section.Title == "硬盘");
            var card = Assert.Single(storage.Cards);
            Assert.Contains(card.Rows, row => row.Label == "卷 C:" && row.Value.Contains("NTFS"));
            // 健康状态没有真实值时不显示
            Assert.DoesNotContain(card.Rows, row => row.Label == "健康状态");
        }

        [Fact]
        public void Detail_CpuAndBiosSections_ShowValuableFields()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                cpu: Cpu(), motherboard: Board(), bios: Bios()));

            var cpu = sections.Single(section => section.Title == "处理器");
            Assert.Contains(cpu.Cards[0].Rows, row => row.Label == "型号" && row.Value.Contains("i9-13980HX"));
            Assert.Contains(cpu.Cards[0].Rows, row => row.Label == "L3 缓存");
            var bios = sections.Single(section => section.Title == "BIOS");
            Assert.Contains(bios.Cards[0].Rows, row => row.Label == "版本" && row.Value == "G834JZ.331");
            Assert.Contains(bios.Cards[0].Rows, row => row.Label == "发布日期" && row.Value == "2024-11-11");
        }

        [Fact]
        public void Detail_OsSection_ShowsUptime()
        {
            var os = new OsInventoryInfo(
                "Microsoft Windows 11 专业版", "10.0.26100", "x64", "G834JZ",
                NowUtc - TimeSpan.FromHours(30), InventorySource.Wmi);

            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(os: os));

            var osSection = sections.Single(section => section.Title == "操作系统");
            Assert.Contains(osSection.Cards[0].Rows, row => row.Label == "计算机名" && row.Value == "G834JZ");
            Assert.Contains(osSection.Cards[0].Rows, row => row.Label == "已运行" && row.Value.Contains("1 天"));
        }
    }
}
