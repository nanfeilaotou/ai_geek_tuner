using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory.Presentation
{
    /// <summary>
    /// V2-M4.5C Gate C：Hardware 详情页左侧静态 Inventory 装配（最终结构）。
    /// 原则：Primary fields 精简可靠，次级/advanced identity（serial/IP/MAC 等）
    /// 以 Secondary 标记低层级渲染；语义不可靠的字段直接不显示
    /// （L2/L3、最大频率、固件虚拟化——见 Gate A 审计）。
    /// </summary>
    public static class HardwareInventoryDetailPresenter
    {
        /// <summary>系统卷标签（无 DriveLetter 且命中 → 不默认展示）。</summary>
        private static readonly IReadOnlyList<string> SystemVolumeLabels =
        [
            "EFI system partition", "Recovery", "恢复分区", "WINRE", "SYSTEM",
        ];

        public static IReadOnlyList<InventoryDisplaySection> BuildSections(
            HardwareInventorySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var sections = new List<InventoryDisplaySection>
            {
                BuildCpu(snapshot),
                BuildMotherboard(snapshot),
                BuildBios(snapshot),
                BuildMemory(snapshot),
                BuildGpu(snapshot),
                BuildMonitors(snapshot),
                BuildStorage(snapshot),
            };

            // V2-M4.5C.1 Gate I：电池（有电池机器才出现；无任何健康判断颜色）。
            if (snapshot.Battery is not null)
            {
                sections.Add(BuildBattery(snapshot.Battery));
            }

            var audio = BuildAudio(snapshot);
            if (audio is not null)
            {
                sections.Add(audio);
            }

            var network = BuildNetwork(snapshot);
            if (network is not null)
            {
                sections.Add(network);
            }

            sections.Add(BuildOs(snapshot));
            return sections;
        }

        // --------------------------------------------------------------- CPU
        private static InventoryDisplaySection BuildCpu(HardwareInventorySnapshot snapshot)
        {
            var cpu = snapshot.Cpu;
            var rows = new List<InventoryDisplayRow>();
            if (cpu is null)
            {
                return new InventoryDisplaySection("处理器", [new InventoryDisplayCard(null, rows)]);
            }

            Add(rows, "型号", cpu.Name);
            Add(rows, "厂商", InventoryDisplayText.NormalizeCpuVendor(cpu.Manufacturer));
            Add(rows, "架构", cpu.Architecture);
            Add(rows, "物理核心", FormatCount(cpu.PhysicalCores, " 核"));
            Add(rows, "逻辑线程", FormatCount(cpu.LogicalCores, " 线程"));
            Add(rows, "基准频率", FormatMegahertz(cpu.BaseClockSpeedMHz));
            // Gate A 审计后不再显示：
            // - 最大频率（Win32_Processor.MaxClockSpeed 实为额定基准，Turbo 无可靠来源）
            // - L2/L3（Win32_CacheMemory 编码跨实现不一致，且逐 instance 值非 package 聚合）
            // - 固件虚拟化（VirtualizationFirmwareEnabled 在 hypervisor 存在时恒 False）
            return new InventoryDisplaySection("处理器", [new InventoryDisplayCard(null, rows)]);
        }

        // ------------------------------------------------------------ 主板 / BIOS
        private static InventoryDisplaySection BuildMotherboard(HardwareInventorySnapshot snapshot)
        {
            var board = snapshot.Motherboard;
            var rows = new List<InventoryDisplayRow>();
            if (board is null)
            {
                return new InventoryDisplaySection("主板", [new InventoryDisplayCard(null, rows)]);
            }

            Add(rows, "厂商", InventoryDisplayText.NormalizeBoardBrand(board.Manufacturer));
            Add(rows, "型号", board.Product);
            Add(rows, "版本", board.Version);
            // Serial 次级且放末尾；占位符必须过滤。
            AddSecondary(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(board.SerialNumber));
            return new InventoryDisplaySection("主板", [new InventoryDisplayCard(null, rows)]);
        }

        private static InventoryDisplaySection BuildBios(HardwareInventorySnapshot snapshot)
        {
            var bios = snapshot.Bios;
            var rows = new List<InventoryDisplayRow>();
            if (bios is null)
            {
                return new InventoryDisplaySection("BIOS", [new InventoryDisplayCard(null, rows)]);
            }

            Add(rows, "厂商", bios.Manufacturer);
            Add(rows, "版本", bios.SmbiosBiosVersion);
            Add(rows, "发布日期", FormatDate(bios.ReleaseDateUtc));
            Add(rows, "SMBIOS 版本", bios.SmbiosVersion);
            return new InventoryDisplaySection("BIOS", [new InventoryDisplayCard(null, rows)]);
        }

        // ---------------------------------------------------------------- 内存
        private static InventoryDisplaySection BuildMemory(HardwareInventorySnapshot snapshot)
        {
            var modules = snapshot.MemoryModules;
            var cards = new List<InventoryDisplayCard>();

            var totalBytes = modules
                .Where(module => module.CapacityBytes.HasValue)
                .Select(module => module.CapacityBytes!.Value)
                .ToArray();
            var speeds = modules
                .Select(module => module.ConfiguredClockSpeedMHz ?? module.SpeedMHz)
                .Where(speed => speed is > 0)
                .Select(speed => speed!.Value)
                .Distinct()
                .OrderBy(speed => speed)
                .ToArray();
            var summaryRows = new List<InventoryDisplayRow>();
            if (totalBytes.Length > 0)
            {
                Add(summaryRows, "总容量", InventoryDisplayText.FormatCapacity(
                    totalBytes.Aggregate(0UL, (acc, value) => acc + value)));
            }

            if (modules.Count > 0)
            {
                Add(summaryRows, "内存条数量", modules.Count + " 条");
            }

            if (speeds.Length > 0)
            {
                Add(summaryRows, "配置频率", string.Join(" / ", speeds.Select(speed => speed + " MHz")));
            }

            cards.Add(new InventoryDisplayCard("总览", summaryRows));

            // 标题用 DIMM 1 / DIMM 2（不用 raw locator 当标题）；
            // BankLabel 重复 BANK 0 无额外价值，不显示。
            foreach (var (module, index) in modules.Select((module, index) => (module, index)))
            {
                var rows = new List<InventoryDisplayRow>();
                Add(rows, "容量", module.CapacityBytes.HasValue
                    ? InventoryDisplayText.FormatCapacity(module.CapacityBytes.Value)
                    : null);
                Add(rows, "厂商", module.Manufacturer);
                var generation = MemoryInventoryMapper.MapMemoryGeneration(module.SmbiosMemoryType);
                if (generation is not null
                    && (module.ConfiguredClockSpeedMHz ?? module.SpeedMHz) is > 0)
                {
                    Add(rows, "规格", generation + "-"
                        + (module.ConfiguredClockSpeedMHz ?? module.SpeedMHz)!.Value
                            .ToString(CultureInfo.InvariantCulture));
                }

                Add(rows, "插槽", module.DeviceLocator);
                Add(rows, "Part Number", module.PartNumber);
                Add(rows, "配置频率", FormatMegahertz(module.ConfiguredClockSpeedMHz));
                Add(rows, "形态", module.FormFactor);
                AddSecondary(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(module.SerialNumber));
                cards.Add(new InventoryDisplayCard("DIMM " + (index + 1), rows));
            }

            return new InventoryDisplaySection("内存", cards);
        }

        // ---------------------------------------------------------------- 显卡
        private static InventoryDisplaySection BuildGpu(HardwareInventorySnapshot snapshot)
        {
            // Gate 0.2：Basic Render Driver 只在系统无其它 GPU 时作为 fallback 出现。
            var gpus = GpuDisplayPolicy.SelectUserFacingGpus(snapshot.Gpus);
            var cards = new List<InventoryDisplayCard>();
            foreach (var gpu in gpus)
            {
                var rows = new List<InventoryDisplayRow>();
                Add(rows, "厂商", gpu.Vendor);
                Add(rows, "驱动版本", gpu.DriverVersion);
                Add(rows, "驱动日期", FormatDate(gpu.DriverDateUtc));
                // iGPU 的几十 MB 预留量不展示（GpuDisplayPolicy 阈值）。
                if (GpuDisplayPolicy.ShouldReportDedicatedVram(gpu.DedicatedVideoMemoryBytes))
                {
                    Add(rows, "专用显存", InventoryDisplayText.FormatCapacity(
                        gpu.DedicatedVideoMemoryBytes!.Value));
                }

                // V2-M4.5C.1 Gate E：Vendor/Device ID、完整 PNP ID、共享显存
                // 一并从普通 UI 移除（底层 Inventory 数据保留）。
                // Gate C：SharedSystemMemory 与完整 PNP Device ID 默认隐藏
                // （raw 数据保留，普通页面不需要占整行）。
                cards.Add(new InventoryDisplayCard(gpu.Name ?? "显卡", rows));
            }

            if (cards.Count == 0)
            {
                cards.Add(new InventoryDisplayCard(null, []));
            }

            return new InventoryDisplaySection("显卡", cards);
        }

        // -------------------------------------------------------------- 显示器
        private static InventoryDisplaySection BuildMonitors(HardwareInventorySnapshot snapshot)
        {
            var cards = new List<InventoryDisplayCard>();
            foreach (var (monitor, index) in snapshot.Monitors.Select((m, i) => (m, i)))
            {
                var title = FirstKnown(monitor.FriendlyName, monitor.EdidIdentity, monitor.ProductCode)
                    ?? "显示器 " + (index + 1);
                var rows = new List<InventoryDisplayRow>();
                Add(rows, "制造商", monitor.ManufacturerCode);
                Add(rows, "产品代码", monitor.ProductCode);
                if (monitor.DiagonalInches is > 0)
                {
                    Add(rows, "物理尺寸", string.Create(CultureInfo.InvariantCulture,
                        $"{monitor.DiagonalInches.Value:0.#}\u0022"));
                }

                Add(rows, "当前分辨率", monitor.CurrentResolution);
                if (monitor.CurrentRefreshRateHz is > 0)
                {
                    Add(rows, "刷新率", string.Create(CultureInfo.InvariantCulture,
                        $"{monitor.CurrentRefreshRateHz.Value.ToString("0.#", CultureInfo.InvariantCulture)} Hz"));
                }

                if (monitor.IsPrimary.HasValue)
                {
                    Add(rows, "主屏", monitor.IsPrimary.Value ? "是" : "否");
                }

                Add(rows, "生产年份", monitor.YearOfManufacture.HasValue
                    ? monitor.YearOfManufacture.Value.ToString(CultureInfo.InvariantCulture)
                    : null);
                AddSecondary(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(monitor.SerialNumber));
                cards.Add(new InventoryDisplayCard(title, rows));
            }

            if (cards.Count == 0)
            {
                cards.Add(new InventoryDisplayCard(null, []));
            }

            return new InventoryDisplaySection("显示器", cards);
        }

        // ---------------------------------------------------------------- 硬盘
        private static InventoryDisplaySection BuildStorage(HardwareInventorySnapshot snapshot)
        {
            var cards = new List<InventoryDisplayCard>();
            foreach (var (disk, index) in snapshot.Disks.Select((d, i) => (d, i)))
            {
                var title = FirstKnown(disk.FriendlyName, disk.Model) ?? "磁盘 " + (index + 1);
                var rows = new List<InventoryDisplayRow>();
                if (disk.SizeBytes.HasValue)
                {
                    Add(rows, "容量", InventoryDisplayText.FormatCapacity(disk.SizeBytes.Value));
                }

                Add(rows, "总线", disk.BusType);
                Add(rows, "介质", disk.MediaType);
                Add(rows, "固件", disk.FirmwareVersion);
                Add(rows, "健康状态", disk.HealthStatus);
                AddSecondary(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(disk.SerialNumber));

                // Gate C：只默认显示用户可见卷（有盘符或有明确非系统卷标）；
                // EFI/Recovery/隐藏分区留在底层，不铺开。
                foreach (var (partition, partitionIndex) in disk.Partitions
                    .Select((p, i) => (p, i)))
                {
                    if (!IsUserVisibleVolume(partition))
                    {
                        continue;
                    }

                    var label = FirstKnown(partition.DriveLetter)
                        ?? "分区 " + (partitionIndex + 1);
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(partition.Label))
                    {
                        parts.Add(partition.Label.Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(partition.FileSystem))
                    {
                        parts.Add(partition.FileSystem.Trim());
                    }

                    if (partition.SizeBytes.HasValue)
                    {
                        parts.Add(InventoryDisplayText.FormatCapacity(partition.SizeBytes.Value));
                    }

                    if (partition.FreeSpaceBytes.HasValue)
                    {
                        parts.Add("剩余 " + InventoryDisplayText.FormatCapacity(partition.FreeSpaceBytes.Value));
                    }

                    rows.Add(new InventoryDisplayRow(
                        "卷 " + label,
                        parts.Count > 0 ? string.Join(" · ", parts) : "—"));
                }

                cards.Add(new InventoryDisplayCard(title, rows));
            }

            if (cards.Count == 0)
            {
                cards.Add(new InventoryDisplayCard(null, []));
            }

            return new InventoryDisplaySection("硬盘", cards);
        }

        // ---------------------------------------------------------------- 电池
        private static InventoryDisplaySection BuildBattery(BatteryInfo battery)
        {
            var rows = new List<InventoryDisplayRow>();

            string state = battery.Charging
                ? "充电中"
                : battery.Discharging
                    ? "放电中"
                    : battery.PowerOnline ? "已接通电源" : "使用电池";
            Add(rows, "状态", state);

            Add(rows, "当前电量", battery.ChargePercent.HasValue
                ? battery.ChargePercent.Value.ToString(CultureInfo.InvariantCulture) + " %"
                : null);
            Add(rows, "设计容量", FormatWatthours(battery.DesignCapacityMWh));
            Add(rows, "满充容量", FormatWatthours(battery.FullChargeCapacityMWh));
            Add(rows, "健康度", battery.HealthPercent.HasValue
                ? battery.HealthPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + " %"
                : null);
            Add(rows, "损耗", battery.WearPercent.HasValue
                ? battery.WearPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + " %"
                : null);
            Add(rows, "当前容量", FormatWatthours(battery.RemainingCapacityMWh));
            Add(rows, "电压", battery.VoltageMillivolts is > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{battery.VoltageMillivolts.Value / 1000d:0.##} V")
                : null);
            // 放电/充电速率只在真实非零时出现（静置时 sources 报 0，不渲染噪音行）。
            Add(rows, "放电速率", battery.DischargeRateMilliwatts is > 0
                ? FormatMilliwatts(battery.DischargeRateMilliwatts.Value)
                : null);
            Add(rows, "充电速率", battery.ChargeRateMilliwatts is > 0
                ? FormatMilliwatts(battery.ChargeRateMilliwatts.Value)
                : null);

            return new InventoryDisplaySection("电池", [new InventoryDisplayCard(null, rows)]);
        }

        private static string? FormatWatthours(uint? milliwattHours) =>
            milliwattHours is > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{milliwattHours.Value / 1000d:0.#} Wh")
                : null;

        private static string FormatMilliwatts(uint milliwatts) =>
            string.Create(CultureInfo.InvariantCulture, $"{milliwatts / 1000d:0.#} W");

        /// <summary>用户可见卷：有 DriveLetter，或有明确用户卷标（非系统分区）。</summary>
        public static bool IsUserVisibleVolume(StoragePartitionInfo partition)
        {
            if (!string.IsNullOrWhiteSpace(partition.DriveLetter))
            {
                return true;
            }

            var label = partition.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            foreach (var systemLabel in SystemVolumeLabels)
            {
                if (label.Contains(systemLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------- 声卡
        private static InventoryDisplaySection? BuildAudio(HardwareInventorySnapshot snapshot)
        {
            // V2-M4.5C.1 Gate G：默认"音频控制器"只列 meaningful hardware
            // controller；NVIDIA Virtual Audio / SteelSeries Sonar / VB-Audio
            // Cable 等虚拟/软件组件绝不进本区（endpoint 区仍保留并标 Virtual）。
            var controllers = (snapshot.AudioControllers ?? Array.Empty<AudioControllerInfo>())
                .Where(controller => !string.IsNullOrWhiteSpace(controller.Name))
                .Where(controller => DashboardInventoryPresenter.IsMeaningfulAudioController(controller.Name))
                .ToArray();
            var playback = snapshot.AudioDevices
                .Where(device => device.Direction == AudioEndpointDirection.Playback)
                .ToArray();
            var capture = snapshot.AudioDevices
                .Where(device => device.Direction == AudioEndpointDirection.Capture)
                .ToArray();
            if (controllers.Length == 0 && playback.Length == 0 && capture.Length == 0)
            {
                return null;
            }

            var cards = new List<InventoryDisplayCard>();
            // Gate C：controller 与 endpoint 不再拼同一行；设备为"列表型条目"
            // （Label 空 → UI 整行渲染名称）。
            var controllerRows = controllers
                .Select(controller => new InventoryDisplayRow("", controller.Name!.Trim()))
                .ToArray();
            if (controllerRows.Length > 0)
            {
                cards.Add(new InventoryDisplayCard("音频控制器", controllerRows));
            }

            var playbackRows = playback
                .Select(device => new InventoryDisplayRow("", DescribeEndpoint(device)))
                .ToArray();
            if (playbackRows.Length > 0)
            {
                cards.Add(new InventoryDisplayCard("播放设备", playbackRows));
            }

            var captureRows = capture
                .Select(device => new InventoryDisplayRow("", DescribeEndpoint(device)))
                .ToArray();
            if (captureRows.Length > 0)
            {
                cards.Add(new InventoryDisplayCard("录制设备", captureRows));
            }

            return new InventoryDisplaySection("声卡", cards);
        }

        private static string DescribeEndpoint(AudioDeviceInfo device)
        {
            var parts = new List<string>();
            if (device.IsDefault)
            {
                parts.Insert(0, "默认");
            }

            if (!string.IsNullOrWhiteSpace(device.State))
            {
                parts.Add(device.State.Trim());
            }

            var star = device.IsDefault ? "★ " : string.Empty;
            var name = (device.FriendlyName ?? "设备").Trim();
            if (IsVirtualEndpoint(name))
            {
                parts.Add("Virtual");
            }

            return star + name + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : string.Empty);
        }

        /// <summary>obvious 虚拟音频端点（VB-Cable 等）标记 Virtual，不隐藏（详情页保留）。</summary>
        public static bool IsVirtualEndpoint(string name)
        {
            foreach (var marker in new[] { "VB-Audio", "VB-Cable", "Virtual Audio", "CABLE Input", "CABLE Output" })
            {
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ---------------------------------------------------------------- 网卡
        private static InventoryDisplaySection? BuildNetwork(HardwareInventorySnapshot snapshot)
        {
            var adapters = snapshot.NetworkAdapters;
            if (adapters.Count == 0)
            {
                return null;
            }

            var cards = new List<InventoryDisplayCard>
            {
                BuildAdapterGroup("物理网络适配器", adapters.Where(a => !a.IsVirtual)),
                BuildAdapterGroup("虚拟网络适配器", adapters.Where(a => a.IsVirtual)),
            };
            cards.RemoveAll(card => card.Rows.Count == 0);
            return new InventoryDisplaySection("网卡", cards);
        }

        private static InventoryDisplayCard BuildAdapterGroup(
            string title, IEnumerable<NetworkAdapterInventoryInfo> adapters)
        {
            var rows = new List<InventoryDisplayRow>();
            foreach (var adapter in adapters)
            {
                var model = FirstKnown(adapter.Description, adapter.Name) ?? "适配器";
                var headline = adapter.IsVirtual ? model + "（虚拟）" : model;
                rows.Add(new InventoryDisplayRow("", headline));

                if (adapter.IsVirtual)
                {
                    // V2-M4.5C.1 Gate H：虚拟适配器只显示 名称/状态/链路速度/虚拟，
                    // 绝不为 VMware VMnet 展开完整 IPv6/DNS dump（底层保留）。
                    Add(rows, "状态", adapter.OperationalStatus);
                    if (adapter.LinkSpeedBps is > 0)
                    {
                        Add(rows, "链路速度", FormatLinkSpeed(adapter.LinkSpeedBps.Value));
                    }

                    continue;
                }

                // 物理适配器默认字段：名称/连接名/类型/状态/链路速度/IPv4；
                // MAC 次级。IPv6/DHCP/网关/DNS 本轮不展示（model 数据保留）。
                Add(rows, "连接名", adapter.Name);
                Add(rows, "类型", adapter.InterfaceType);
                Add(rows, "状态", adapter.OperationalStatus);
                if (adapter.LinkSpeedBps is > 0)
                {
                    Add(rows, "链路速度", FormatLinkSpeed(adapter.LinkSpeedBps.Value));
                }

                Add(rows, "IPv4", JoinAll(adapter.IPv4Addresses));
                AddSecondary(rows, "MAC", adapter.MacAddress);
            }

            return new InventoryDisplayCard(title, rows);
        }

        // ------------------------------------------------------------- 操作系统
        private static InventoryDisplaySection BuildOs(HardwareInventorySnapshot snapshot)
        {
            var os = snapshot.Os;
            var rows = new List<InventoryDisplayRow>();
            if (os is null)
            {
                return new InventoryDisplaySection("操作系统", [new InventoryDisplayCard(null, rows)]);
            }

            Add(rows, "系统名称", os.Name);
            Add(rows, "版本", os.Version);
            Add(rows, "架构", os.Architecture);
            Add(rows, "计算机名", os.ComputerName);
            if (os.LastBootUtc.HasValue)
            {
                Add(rows, "最近启动", os.LastBootUtc.Value.ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                Add(rows, "已运行", FormatUptime(snapshot.CollectedAtUtc - os.LastBootUtc.Value));
            }

            return new InventoryDisplaySection("操作系统", [new InventoryDisplayCard(null, rows)]);
        }

        // ------------------------------------------------------------- helpers
        private static void Add(ICollection<InventoryDisplayRow> rows, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new InventoryDisplayRow(label, value.Trim()));
            }
        }

        private static void AddSecondary(ICollection<InventoryDisplayRow> rows, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new InventoryDisplayRow(label, value.Trim(), Secondary: true));
            }
        }

        private static string? FirstKnown(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        private static string? JoinAll(IReadOnlyList<string> values)
        {
            var known = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return known.Length > 0 ? string.Join(" / ", known) : null;
        }

        private static string? FormatCount(uint? value, string suffix) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + suffix : null;

        private static string? FormatMegahertz(uint? value) =>
            value is > 0
                ? value.Value.ToString("#,0", CultureInfo.InvariantCulture) + " MHz"
                : null;

        private static string? FormatDate(DateTimeOffset? value) =>
            value.HasValue
                ? value.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;

        internal static string FormatLinkSpeed(ulong bitsPerSecond) =>
            bitsPerSecond >= 1_000_000_000
                ? string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1_000_000_000d:0.#} Gbps")
                : string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond / 1_000_000d:0.#} Mbps");

        internal static string FormatUptime(TimeSpan uptime)
        {
            if (uptime < TimeSpan.Zero)
            {
                return "—";
            }

            var days = (int)uptime.TotalDays;
            var hours = uptime.Hours;
            var minutes = uptime.Minutes;
            if (days > 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{days} 天 {hours} 小时");
            }

            if (hours > 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{hours} 小时 {minutes} 分");
            }

            return string.Create(CultureInfo.InvariantCulture, $"{minutes} 分钟");
        }
    }
}
