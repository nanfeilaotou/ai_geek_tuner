using System;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
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
            AudioControllerInfo[]? audioControllers = null,
            BatteryInfo? battery = null) =>
            new(
                cpu, motherboard, bios, os,
                memory ?? [],
                gpus ?? [],
                disks ?? [],
                monitors ?? [],
                audio ?? [],
                network ?? [],
                NowUtc,
                audioControllers,
                battery);

        // ---------------------------------------------------------- fixtures
        private static CpuInventoryInfo Cpu() => new(
            "13th Gen Intel(R) Core(TM) i9-13980HX", "GenuineIntel", "x64",
            24, 32, 5800, 2200, 20480, 36864, true, InventorySource.Wmi);

        private static MotherboardInventoryInfo Board() => new(
            "ASUSTeK COMPUTER INC.", "G834JZ", "1.0", "ABCDEF0123", InventorySource.Wmi);

        private static BiosInventoryInfo Bios() => new(
            "American Megatrends International, LLC.", "G834JZ.331",
            new DateTimeOffset(2024, 11, 11, 0, 0, 0, TimeSpan.Zero), "3.3", InventorySource.Wmi);

        private static MemoryModuleInfo Dimm(string locator, string part, ulong gb, uint? ddrType = 34) => new(
            locator, "ChannelA", gb * 1024L * 1024L * 1024L, "SK Hynix", part,
            "12345678", 5600, 5600, "SODIMM", 64, 64, InventorySource.Wmi, ddrType);

        private static GpuInventoryInfo Gpu(string name, ulong? vram = null) => new(
            name, "NVIDIA", 0x10DE, 0x24A0, null, "546.33",
            new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero),
            vram, 8_589_934_592, InventorySource.DXGI);

        private static MonitorInventoryInfo Monitor(
            string name, string? resolution = "2560 × 1600", double? hz = 240,
            double? diagonal = 17.7, bool? primary = true) => new(
            name, "BOE", "0B35", "SERIAL123", 2022, 38, 30, diagonal,
            resolution, hz, primary, name, InventorySource.WmiMonitor);

        private static StorageDiskInventoryInfo Disk(string model, ulong? sizeBytes = 4_096_854_555_290) => new(
            model, model, "PSBH63410502545", "F1.0", sizeBytes, "NVMe", "SSD", null,
            0, [new StoragePartitionInfo("C:", "NTFS", "System", 2_000_000_000_000, 500_000_000_000)],
            InventorySource.Wmi);

        private static AudioDeviceInfo Endpoint(
            string name, AudioEndpointDirection direction, bool isDefault = false) => new(
            name, "id", direction, "Active", isDefault, InventorySource.CoreAudio);

        private static NetworkAdapterInventoryInfo Adapter(
            string name, bool isVirtual, string? ipv4 = null) => new(
            name, name + " 描述", "Ethernet", "Up", 1_000_000_000, "AA-BB",
            ipv4 is null ? ["192.168.1.10"] : [ipv4], [], true, [], [], isVirtual,
            InventorySource.WindowsNetwork);

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
        public void Dashboard_AudioRow_Conditional_And_PhysicalOnly()
        {
            var withoutAudio = DashboardInventoryPresenter.BuildRows(Snapshot());
            Assert.DoesNotContain(withoutAudio, row => row.Label == "声卡");

            // Gate B：endpoint 不是硬件——只有 endpoint 时 Dashboard 不显示声卡行。
            var endpointsOnly = DashboardInventoryPresenter.BuildRows(Snapshot(
                audio: [Endpoint("扬声器 (Realtek)", AudioEndpointDirection.Playback)]));
            Assert.DoesNotContain(endpointsOnly, row => row.Label == "声卡");

            var withControllers = DashboardInventoryPresenter.BuildRows(Snapshot(
                audioControllers:
                [
                    new AudioControllerInfo("Realtek High Definition Audio", "Realtek", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("NVIDIA High Definition Audio", "NVIDIA", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("NVIDIA Virtual Audio Device", "NVIDIA", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("SteelSeries Sonar - Game", "SteelSeries", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("VB-Audio Cable", "VB-Audio", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("适用于蓝牙® 音频的英特尔® 智音技术", "Intel", "OK", null, InventorySource.Wmi),
                ]));
            var controllerRow = withControllers.Single(row => row.Label == "声卡");
            // V2-M4.5C.1 Gate D：用户验收点名的虚拟/软件组件全部不得出现在 Dashboard。
            Assert.Contains("Realtek High Definition Audio", controllerRow.Value);
            Assert.Contains("NVIDIA High Definition Audio", controllerRow.Value);
            Assert.DoesNotContain("Virtual", controllerRow.Value);
            Assert.DoesNotContain("Sonar", controllerRow.Value);
            Assert.DoesNotContain("VB-Audio", controllerRow.Value);
            Assert.DoesNotContain("蓝牙", controllerRow.Value);
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
            Assert.Contains("RTX 4080 Laptop GPU · 11.7 GB", gpuRow.Value);
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
            Assert.Equal("32 GB DDR5 · 5600 MHz · 2×16 GB · SK Hynix", memoryRow.Value);
        }

        [Fact]
        public void Dashboard_MemoryRow_UnequalCapacities_NotFakeMultiplier()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                memory:
                [
                    Dimm("A", "P1", 16),
                    Dimm("B", "P2", 8),
                ]));

            var memoryRow = rows.Single(row => row.Label == "内存");
            Assert.Contains("24 GB DDR5", memoryRow.Value);
            Assert.Contains("16 GB + 8 GB", memoryRow.Value);
            Assert.DoesNotContain("2×", memoryRow.Value);
        }

        [Fact]
        public void Dashboard_ProcessorRow_ShowsNameOnly()
        {
            // Gate B：Dashboard 删除 GenuineIntel 厂商噪音。
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(cpu: Cpu()));

            var cpuRow = rows.Single(row => row.Label == "处理器");
            Assert.Equal("13th Gen Intel(R) Core(TM) i9-13980HX", cpuRow.Value);
        }

        [Fact]
        public void Dashboard_MotherboardRow_UsesObviousBrandAlias()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(motherboard: Board()));

            Assert.Contains("ASUS · G834JZ", rows.Single(row => row.Label == "主板").Value);
        }

        [Fact]
        public void Dashboard_StorageRow_UsesTbFormatter()
        {
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                disks:
                [
                    Disk("Predator SSD GM7 M.2 4TB"),
                    Disk("SAMSUNG MZVL21T0HCLR-00B00", sizeBytes: 1_024_246_425_994),
                ]));

            var storageRow = rows.Single(row => row.Label == "硬盘");
            Assert.Contains("3.73 TB", storageRow.Value);
            Assert.Contains("953.9 GB", storageRow.Value);
            Assert.DoesNotContain("3815.4 GB", storageRow.Value);
        }

        [Fact]
        public void Dashboard_NetworkRow_ShowsPhysicalAdapterModels()
        {
            // Gate B：显示真实物理型号（Description），不是 WLAN/Ethernet 连接别名。
            var wifi = new NetworkAdapterInventoryInfo(
                "WLAN", "Intel Wi-Fi 6E AX211 160MHz", "Ethernet", "Up",
                1_200_000_000, null, [], [], null, [], [], false, InventorySource.WindowsNetwork);
            var rows = DashboardInventoryPresenter.BuildRows(Snapshot(
                network:
                [
                    wifi,
                    Adapter("VMware Network Adapter VMnet1", isVirtual: true),
                ]));

            var networkRow = rows.Single(row => row.Label == "网卡");
            Assert.Contains("Intel Wi-Fi 6E AX211 160MHz", networkRow.Value);
            Assert.DoesNotContain("VMware", networkRow.Value);
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
            // Gate C：标题用 DIMM 1 / DIMM 2（raw locator 放插槽字段，不当标题）。
            Assert.Contains("DIMM 1", titles);
            Assert.Contains("DIMM 2", titles);
            var dimm1 = memory.Cards.Single(card => card.Title == "DIMM 1");
            Assert.Contains(dimm1.Rows, row => row.Label == "Part Number" && row.Value == "PART-A");
            Assert.Contains(dimm1.Rows, row => row.Label == "插槽" && row.Value == "Controller0-ChannelA-DIMM0");
            Assert.Contains(dimm1.Rows, row => row.Label == "规格" && row.Value == "DDR5-5600");
            var dimm2 = memory.Cards.Single(card => card.Title == "DIMM 2");
            Assert.Contains(dimm2.Rows, row => row.Label == "Part Number" && row.Value == "PART-B");
            // BankLabel（重复 BANK 0）不显示。
            Assert.DoesNotContain(dimm1.Rows, row => row.Label == "Bank");
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
            // Gate C/E：共享显存、完整 PNP ID、Vendor/Device ID 均不在普通 UI。
            Assert.DoesNotContain(nvidia.Rows, row => row.Label == "共享显存");
            Assert.DoesNotContain(nvidia.Rows, row => row.Label == "PNP 设备 ID");
            Assert.DoesNotContain(nvidia.Rows, row => row.Label == "Vendor / Device ID");
            // Gate E 保留字段：名称/厂商/驱动版本/驱动日期/专用显存。
            Assert.Contains(nvidia.Rows, row => row.Label == "厂商");
            Assert.Contains(nvidia.Rows, row => row.Label == "驱动版本");
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
        public void Detail_AudioControllers_ExcludeVirtualSoftwareComponents()
        {
            // V2-M4.5C.1 Gate G：虚拟/软件 controller 不进"音频控制器"section；
            // endpoint 区仍保留并标 Virtual。
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                audio:
                [
                    Endpoint("CABLE Input (VB-Audio)", AudioEndpointDirection.Playback),
                ],
                audioControllers:
                [
                    new AudioControllerInfo("Realtek High Definition Audio", "Realtek", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("NVIDIA Virtual Audio Device", "NVIDIA", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("SteelSeries Sonar - Game", "SteelSeries", "OK", null, InventorySource.Wmi),
                    new AudioControllerInfo("VB-Audio Cable", "VB-Audio", "OK", null, InventorySource.Wmi),
                ]));

            var audio = sections.Single(section => section.Title == "声卡");
            var controllerCard = audio.Cards.Single(card => card.Title == "音频控制器");
            var controllerValues = controllerCard.Rows.Select(row => row.Value).ToArray();
            Assert.Single(controllerValues);
            Assert.Contains("Realtek High Definition Audio", controllerValues[0]);

            var playback = audio.Cards.Single(card => card.Title == "播放设备");
            Assert.Contains(playback.Rows, row => row.Value.Contains("Virtual"));
        }

        [Fact]
        public void Detail_NetworkSection_GroupsPhysicalAndVirtual()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                network:
                [
                    Adapter("Wi-Fi", isVirtual: false),
                    Adapter("VMware Network Adapter VMnet1", isVirtual: true),
                ]));

            var network = sections.Single(section => section.Title == "网卡");
            var titles = network.Cards.Select(card => card.Title).ToArray();
            // Gate C：物理在前，虚拟明确分组/标记；IP/MAC 等为次级行。
            Assert.Equal(new[] { "物理网络适配器", "虚拟网络适配器" }, titles);
            var virtualCard = network.Cards[1];
            Assert.Contains(virtualCard.Rows, row => row.Value.Contains("VMware") && row.Value.Contains("虚拟"));
            var physicalCard = network.Cards[0];
            Assert.Contains(physicalCard.Rows, row => row.Label == "MAC" && row.Secondary);
        }

        [Fact]
        public void Detail_NetworkSection_VirtualAdapterReduced_PhysicalKeepsIpV4Only()
        {
            // V2-M4.5C.1 Gate H：虚拟适配器只显示 名称/状态/链路速度/虚拟；
            // 物理适配器默认到 IPv4 + MAC(次级)，不再展开 IPv6/DHCP/网关/DNS dump。
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                network:
                [
                    Adapter("Intel Wi-Fi 6E AX211 160MHz", isVirtual: false, ipv4: "192.168.1.8"),
                    Adapter("VMware Network Adapter VMnet1", isVirtual: true),
                ]));

            var network = sections.Single(section => section.Title == "网卡");
            var virtualCard = network.Cards.Single(card => card.Title == "虚拟网络适配器");
            Assert.DoesNotContain(virtualCard.Rows, row => row.Label == "IPv4");
            Assert.DoesNotContain(virtualCard.Rows, row => row.Label == "MAC");
            Assert.DoesNotContain(virtualCard.Rows, row => row.Label == "连接名");
            Assert.DoesNotContain(virtualCard.Rows, row => row.Label == "类型");

            var physicalCard = network.Cards.Single(card => card.Title == "物理网络适配器");
            Assert.Contains(physicalCard.Rows, row => row.Label == "IPv4" && !row.Secondary);
            Assert.DoesNotContain(physicalCard.Rows, row => row.Label == "IPv6");
            Assert.DoesNotContain(physicalCard.Rows, row => row.Label == "DHCP");
            Assert.DoesNotContain(physicalCard.Rows, row => row.Label == "网关");
            Assert.DoesNotContain(physicalCard.Rows, row => row.Label == "DNS");
        }

        [Fact]
        public void Detail_StorageSection_ShowsVolumes()
        {
            var disk = Disk("Predator SSD GM7 M.2 4TB");
            var withSystemPartitions = new StorageDiskInventoryInfo(
                disk.Model, disk.FriendlyName, disk.SerialNumber, "F1.0", disk.SizeBytes, "NVMe", "SSD", null, 0,
                [
                    new StoragePartitionInfo("C:", "NTFS", "OS", 900_000_000_000, 400_000_000_000),
                    new StoragePartitionInfo(null, "", "EFI system partition", 300_000_000, null),
                    new StoragePartitionInfo(null, null, "Recovery", 1_100_000_000, null),
                    new StoragePartitionInfo(null, "游戏", "NTFS", 26_000_000_000, null),
                ],
                InventorySource.Wmi);
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(disks: [withSystemPartitions]));

            var storage = sections.Single(section => section.Title == "硬盘");
            var card = Assert.Single(storage.Cards);
            // 只默认显示用户可见卷：有盘符或有明确非系统卷标。
            Assert.Contains(card.Rows, row => row.Label == "卷 C:" && row.Value.Contains("NTFS"));
            Assert.Contains(card.Rows, row => row.Label!.StartsWith("卷 分区") && row.Value.Contains("游戏"));
            Assert.DoesNotContain(card.Rows, row => row.Value.Contains("EFI"));
            Assert.DoesNotContain(card.Rows, row => row.Value.Contains("Recovery"));
            // 健康状态没有真实值时不显示
            Assert.DoesNotContain(card.Rows, row => row.Label == "健康状态");
            // serial 次级且放末尾
            var serial = card.Rows.LastOrDefault(row => row.Label == "序列号");
            Assert.NotNull(serial);
            Assert.True(serial!.Secondary);
        }

        [Fact]
        public void Detail_StorageMixedSystemAndUserPartitions_ShowsOnlyUserVolumes()
        {
            // V2-M4.5C.1 Gate F E2E：mapper（char16 无盘符 junk → null 盘符）→
            // presenter（无盘符且无用户卷标的分区不渲染）。普通详情只留 C:/D:。
            IInventoryRow Row2(params (string Key, object? Value)[] values) =>
                new DictionaryInventoryRow(
                    new System.Collections.Generic.Dictionary<string, object?>(
                        values.ToDictionary(v => v.Key, v => v.Value)));

            var disks = new IInventoryRow[]
            {
                Row2(("DeviceId", (object)0ul),
                    ("FriendlyName", "Samsung MZVL21T0HCLR-00B00"),
                    ("Model", "Samsung MZVL21T0HCLR-00B00"),
                    ("SerialNumber", "S6ZPX00R111"),
                    ("FirmwareVersion", "5M2QGXA7"),
                    ("Size", (object)1_000_000_000_000ul),
                    ("BusType", 17u),
                    ("MediaType", 4u),
                    ("HealthStatus", "Healthy")),
            };
            var partitions = new IInventoryRow[]
            {
                Row2(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)272_629_760ul)),
                Row2(("DiskNumber", 0u), ("DriveLetter", 'C'), ("Size", (object)900_000_000_000ul)),
                Row2(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)1_153_433_600ul)),
                Row2(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)27_917_287_424ul)),
                Row2(("DiskNumber", 0u), ("DriveLetter", 'D'), ("Size", (object)60_000_000_000ul)),
            };
            var volumes = new IInventoryRow[]
            {
                Row2(("DriveLetter", "C:"), ("FileSystem", "NTFS"), ("FileSystemLabel", "OS"),
                    ("Size", (object)900_000_000_000ul), ("SizeRemaining", (object)400_000_000_000ul)),
                Row2(("DriveLetter", "D:"), ("FileSystem", "NTFS"), ("FileSystemLabel", "games"),
                    ("Size", (object)60_000_000_000ul), ("SizeRemaining", (object)10_000_000_000ul)),
            };

            var disksInfo = StorageInventoryMapper.Map(disks, partitions, volumes);
            var sections = HardwareInventoryDetailPresenter.BuildSections(
                Snapshot(disks: disksInfo.ToArray()));

            var storage = sections.Single(section => section.Title == "硬盘");
            var card = Assert.Single(storage.Cards);
            var volumeLabels = card.Rows
                .Where(row => row.Label!.StartsWith("卷 ", StringComparison.Ordinal))
                .Select(row => row.Label)
                .ToArray();
            Assert.Equal(new[] { "卷 C", "卷 D" }, volumeLabels);
        }

        [Fact]
        public void Detail_BatterySection_ShowsOnlyExistingFields_NoHealthColors()
        {
            // 本机真机形态：BatteryStaticData 缺失 → 设计容量/健康度/损耗不出行。
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                battery: new BatteryInfo(
                    "R220358", PowerOnline: true, Charging: false, Discharging: false,
                    ChargePercent: 100, DesignCapacityMWh: null, FullChargeCapacityMWh: 63800,
                    RemainingCapacityMWh: 63801, VoltageMillivolts: 16571,
                    ChargeRateMilliwatts: null, DischargeRateMilliwatts: null,
                    HealthPercent: null, WearPercent: null, InventorySource.Wmi)));

            var battery = sections.Single(section => section.Title == "电池");
            var card = Assert.Single(battery.Cards);
            Assert.Contains(card.Rows, row => row.Label == "状态" && row.Value == "已接通电源");
            Assert.Contains(card.Rows, row => row.Label == "当前电量" && row.Value == "100 %");
            Assert.Contains(card.Rows, row => row.Label == "满充容量" && row.Value == "63.8 Wh");
            Assert.Contains(card.Rows, row => row.Label == "当前容量");
            Assert.Contains(card.Rows, row => row.Label == "电压" && row.Value == "16.57 V");
            // 来源不可靠/缺失的字段绝不显示。
            Assert.DoesNotContain(card.Rows, row => row.Label == "设计容量");
            Assert.DoesNotContain(card.Rows, row => row.Label == "健康度");
            Assert.DoesNotContain(card.Rows, row => row.Label == "损耗");
            Assert.DoesNotContain(card.Rows, row => row.Label == "放电速率");
        }

        [Fact]
        public void Detail_BatterySection_ChargingAndWearShown()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                battery: new BatteryInfo(
                    null, PowerOnline: true, Charging: true, Discharging: false,
                    ChargePercent: 47, DesignCapacityMWh: 63800, FullChargeCapacityMWh: 57420,
                    RemainingCapacityMWh: 30000, VoltageMillivolts: 16000,
                    ChargeRateMilliwatts: 25000, DischargeRateMilliwatts: null,
                    HealthPercent: 90.0, WearPercent: 10.0, InventorySource.Wmi)));

            var card = Assert.Single(
                sections.Single(section => section.Title == "电池").Cards);
            Assert.Contains(card.Rows, row => row.Label == "状态" && row.Value == "充电中");
            Assert.Contains(card.Rows, row => row.Label == "健康度" && row.Value == "90 %");
            Assert.Contains(card.Rows, row => row.Label == "损耗" && row.Value == "10 %");
            Assert.Contains(card.Rows, row => row.Label == "设计容量" && row.Value == "63.8 Wh");
            Assert.Contains(card.Rows, row => row.Label == "充电速率" && row.Value == "25 W");
            Assert.DoesNotContain(card.Rows, row => row.Label == "放电速率");
        }

        [Fact]
        public void Detail_CpuAndBiosSections_ShowValuableFields()
        {
            var sections = HardwareInventoryDetailPresenter.BuildSections(Snapshot(
                cpu: Cpu(), motherboard: Board(), bios: Bios()));

            var cpu = sections.Single(section => section.Title == "处理器");
            Assert.Contains(cpu.Cards[0].Rows, row => row.Label == "型号" && row.Value.Contains("i9-13980HX"));
            // Gate A：语义不可靠的字段默认不显示（详见 mapper 审计注释）。
            Assert.DoesNotContain(cpu.Cards[0].Rows, row => row.Label == "L3 缓存");
            Assert.DoesNotContain(cpu.Cards[0].Rows, row => row.Label == "L2 缓存");
            Assert.DoesNotContain(cpu.Cards[0].Rows, row => row.Label == "最大频率");
            Assert.DoesNotContain(cpu.Cards[0].Rows, row => row.Label == "固件虚拟化");
            Assert.Contains(cpu.Cards[0].Rows, row => row.Label == "基准频率");
            // 厂商规范化：GenuineIntel → Intel。
            Assert.Contains(cpu.Cards[0].Rows, row => row.Label == "厂商" && row.Value == "Intel");
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
