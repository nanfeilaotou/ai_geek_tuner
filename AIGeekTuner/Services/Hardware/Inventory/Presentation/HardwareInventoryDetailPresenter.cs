using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory.Presentation
{
    /// <summary>
    /// V2-M4.5B Gate B：Hardware 详情页左侧静态 Inventory 装配。
    /// 每个 section 一张卡片组：CPU / 主板 / BIOS / 内存（总览 + 每条 DIMM）/ 显卡
    /// / 显示器（每台一卡）/ 硬盘（每盘一卡 + 卷）/ 声卡（控制器 + 端点）/
    /// 网卡（物理 + 虚拟明确标记）/ 操作系统。缺字段直接省略行。
    /// Basic Render Driver 不生成普通显卡卡片（Gate 0.2）。
    /// 纯静态映射，不依赖真实机器（Gate I）。
    /// </summary>
    public static class HardwareInventoryDetailPresenter
    {
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
            Add(rows, "厂商", cpu.Manufacturer);
            Add(rows, "架构", cpu.Architecture);
            Add(rows, "物理核心", FormatCount(cpu.PhysicalCores, " 核"));
            Add(rows, "逻辑线程", FormatCount(cpu.LogicalCores, " 线程"));
            Add(rows, "基础频率", FormatMegahertz(cpu.BaseClockSpeedMHz));
            Add(rows, "最大频率", FormatMegahertz(cpu.MaxClockSpeedMHz));
            Add(rows, "L2 缓存", FormatKilobytes(cpu.L2CacheSizeKB));
            Add(rows, "L3 缓存", FormatKilobytes(cpu.L3CacheSizeKB));
            if (cpu.VirtualizationFirmwareEnabled.HasValue)
            {
                Add(rows, "固件虚拟化", cpu.VirtualizationFirmwareEnabled.Value ? "已启用" : "已禁用");
            }

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

            Add(rows, "厂商", board.Manufacturer);
            Add(rows, "型号", board.Product);
            Add(rows, "版本", board.Version);
            // 本地详情页允许显示可靠 serial；占位符必须过滤（HardwarePlaceholderFilter）。
            Add(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(board.SerialNumber));
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
                Add(summaryRows, "总容量", DashboardInventoryPresenter.FormatBytes(
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

            foreach (var (module, index) in modules.Select((module, index) => (module, index)))
            {
                var title = FirstKnown(module.DeviceLocator, module.BankLabel) ?? "DIMM " + (index + 1);
                var rows = new List<InventoryDisplayRow>();
                Add(rows, "插槽", module.DeviceLocator);
                Add(rows, "Bank", module.BankLabel);
                Add(rows, "容量", module.CapacityBytes.HasValue
                    ? DashboardInventoryPresenter.FormatBytes(module.CapacityBytes.Value)
                    : null);
                Add(rows, "厂商", module.Manufacturer);
                Add(rows, "Part Number", module.PartNumber);
                Add(rows, "配置频率", FormatMegahertz(module.ConfiguredClockSpeedMHz));
                Add(rows, "标称频率", FormatMegahertz(module.SpeedMHz));
                Add(rows, "形态", module.FormFactor);
                // 次级详情：serial 允许在本地详情页显示，但必须过滤占位符。
                Add(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(module.SerialNumber));
                cards.Add(new InventoryDisplayCard(title, rows));
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
                    Add(rows, "专用显存", DashboardInventoryPresenter.FormatBytes(
                        gpu.DedicatedVideoMemoryBytes!.Value));
                }

                if (gpu.SharedSystemMemoryBytes is > 0)
                {
                    Add(rows, "共享显存", DashboardInventoryPresenter.FormatBytes(
                        gpu.SharedSystemMemoryBytes.Value));
                }

                Add(rows, "Vendor / Device ID", FormatPciIds(gpu.VendorId, gpu.DeviceId));
                Add(rows, "PNP 设备 ID", gpu.PnpDeviceId);
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
                        $"约 {monitor.DiagonalInches.Value:0.#} 英寸"));
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
                Add(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(monitor.SerialNumber));
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
                    Add(rows, "容量", DashboardInventoryPresenter.FormatBytes(disk.SizeBytes.Value));
                }

                Add(rows, "总线", disk.BusType);
                Add(rows, "介质", disk.MediaType);
                Add(rows, "固件", disk.FirmwareVersion);
                Add(rows, "健康状态", disk.HealthStatus);
                Add(rows, "序列号", HardwarePlaceholderFilter.SanitizeSerialNumber(disk.SerialNumber));

                foreach (var (partition, partitionIndex) in disk.Partitions
                    .Select((p, i) => (p, i)))
                {
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
                        parts.Add(DashboardInventoryPresenter.FormatBytes(partition.SizeBytes.Value));
                    }

                    if (partition.FreeSpaceBytes.HasValue)
                    {
                        parts.Add("剩余 " + DashboardInventoryPresenter.FormatBytes(partition.FreeSpaceBytes.Value));
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

        // ---------------------------------------------------------------- 声卡
        private static InventoryDisplaySection? BuildAudio(HardwareInventorySnapshot snapshot)
        {
            var controllers = (snapshot.AudioControllers ?? Array.Empty<AudioControllerInfo>())
                .Where(controller => !string.IsNullOrWhiteSpace(controller.Name))
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
            var controllerRows = new List<InventoryDisplayRow>();
            foreach (var controller in controllers)
            {
                var parts = new[] { controller.Manufacturer, controller.Status }
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part!.Trim())
                    .ToArray();
                controllerRows.Add(new InventoryDisplayRow(
                    controller.Name!.Trim(),
                    parts.Length > 0 ? string.Join(" · ", parts) : "—"));
            }

            if (controllerRows.Count > 0)
            {
                cards.Add(new InventoryDisplayCard("音频控制器", controllerRows));
            }

            var playbackRows = playback
                .Select(device => new InventoryDisplayRow(
                    device.FriendlyName ?? "播放设备",
                    DescribeEndpoint(device)))
                .ToArray();
            if (playbackRows.Length > 0)
            {
                cards.Add(new InventoryDisplayCard("播放设备", playbackRows));
            }

            var captureRows = capture
                .Select(device => new InventoryDisplayRow(
                    device.FriendlyName ?? "录制设备",
                    DescribeEndpoint(device)))
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
                parts.Add("默认");
            }

            if (!string.IsNullOrWhiteSpace(device.State))
            {
                parts.Add(device.State.Trim());
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : "—";
        }

        // ---------------------------------------------------------------- 网卡
        private static InventoryDisplaySection? BuildNetwork(HardwareInventorySnapshot snapshot)
        {
            var adapters = snapshot.NetworkAdapters;
            if (adapters.Count == 0)
            {
                return null;
            }

            // 详情页保留物理 + 虚拟适配器；虚拟必须明确标记。
            var ordered = adapters
                .OrderByDescending(adapter => !adapter.IsVirtual)
                .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase);
            var cards = new List<InventoryDisplayCard>();
            foreach (var adapter in ordered)
            {
                var title = FirstKnown(adapter.Name, adapter.Description)
                    ?? (adapter.IsVirtual ? "虚拟网卡" : "网卡");
                if (adapter.IsVirtual)
                {
                    title += "（虚拟）";
                }

                var rows = new List<InventoryDisplayRow>();
                Add(rows, "描述", adapter.Description);
                Add(rows, "类型", adapter.InterfaceType);
                Add(rows, "状态", adapter.OperationalStatus);
                if (adapter.LinkSpeedBps is > 0)
                {
                    Add(rows, "链路速度", FormatLinkSpeed(adapter.LinkSpeedBps.Value));
                }

                Add(rows, "MAC", adapter.MacAddress);
                Add(rows, "IPv4", JoinAll(adapter.IPv4Addresses));
                Add(rows, "IPv6", JoinAll(adapter.IPv6Addresses));
                if (adapter.DhcpEnabled.HasValue)
                {
                    Add(rows, "DHCP", adapter.DhcpEnabled.Value ? "已启用" : "已禁用");
                }

                Add(rows, "网关", JoinAll(adapter.Gateways));
                Add(rows, "DNS", JoinAll(adapter.DnsServers));
                cards.Add(new InventoryDisplayCard(title, rows));
            }

            return new InventoryDisplaySection("网卡", cards);
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

        private static string? FormatKilobytes(uint? value) =>
            value is > 0
                ? value.Value >= 1024
                    ? string.Create(CultureInfo.InvariantCulture, $"{value.Value / 1024d:0.#} MB")
                    : value.Value.ToString(CultureInfo.InvariantCulture) + " KB"
                : null;

        private static string? FormatDate(DateTimeOffset? value) =>
            value.HasValue
                ? value.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;

        private static string? FormatPciIds(uint? vendorId, uint? deviceId)
        {
            if (!vendorId.HasValue && !deviceId.HasValue)
            {
                return null;
            }

            var vendor = vendorId.HasValue
                ? vendorId.Value.ToString("X4", CultureInfo.InvariantCulture)
                : "????";
            var device = deviceId.HasValue
                ? deviceId.Value.ToString("X4", CultureInfo.InvariantCulture)
                : "????";
            return vendor + ":" + device;
        }

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
