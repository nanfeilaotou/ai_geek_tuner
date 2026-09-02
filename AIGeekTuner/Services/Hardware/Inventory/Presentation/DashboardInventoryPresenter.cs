using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory.Presentation
{
    /// <summary>
    /// V2-M4.5B Gate A：Dashboard "电脑详细信息" 行装配（固定顺序）：
    /// 主板 / 处理器 / 内存 / 显卡 / 显示器 / 硬盘（6 个强制行）
    /// + 声卡 / 网卡（仅在检测到时出现）。缺值只省略次级字段。
    /// 纯静态映射，不依赖真实机器（Gate I）。
    /// </summary>
    public static class DashboardInventoryPresenter
    {
        /// <summary>声卡/网卡摘要最多列出的条目数，超出折叠为 "等 N 个设备"。</summary>
        public const int MaxSummaryEntries = 3;

        public static IReadOnlyList<InventoryDisplayRow> BuildRows(
            HardwareInventorySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var rows = new List<InventoryDisplayRow>
            {
                BuildMotherboardRow(snapshot),
                BuildProcessorRow(snapshot),
                BuildMemoryRow(snapshot),
                BuildGpuRow(snapshot),
                BuildMonitorRow(snapshot),
                BuildStorageRow(snapshot),
            };

            var audio = BuildAudioRow(snapshot);
            if (audio is not null)
            {
                rows.Add(audio);
            }

            var network = BuildNetworkRow(snapshot);
            if (network is not null)
            {
                rows.Add(network);
            }

            return rows;
        }

        private static InventoryDisplayRow BuildMotherboardRow(HardwareInventorySnapshot snapshot)
        {
            var board = snapshot.Motherboard;
            var value = JoinParts(board?.Manufacturer, board?.Product);
            return new InventoryDisplayRow("主板", value ?? NotDetected);
        }

        private static InventoryDisplayRow BuildProcessorRow(HardwareInventorySnapshot snapshot)
        {
            var cpu = snapshot.Cpu;
            var value = JoinParts(cpu?.Name, cpu?.Manufacturer);
            return new InventoryDisplayRow("处理器", value ?? NotDetected);
        }

        private static InventoryDisplayRow BuildMemoryRow(HardwareInventorySnapshot snapshot)
        {
            var modules = snapshot.MemoryModules;
            if (modules.Count == 0)
            {
                return new InventoryDisplayRow("内存", NotDetected);
            }

            var parts = new List<string>();

            var totalBytes = modules
                .Where(module => module.CapacityBytes.HasValue)
                .Select(module => module.CapacityBytes!.Value)
                .ToArray();
            if (totalBytes.Length > 0)
            {
                parts.Add(FormatBytes(totalBytes.Aggregate(0UL, (acc, value) => acc + value)));
            }

            var manufacturers = modules
                .Select(module => module.Manufacturer)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (manufacturers.Length > 0)
            {
                parts.Add(string.Join(" / ", manufacturers));
            }

            var speeds = modules
                .Select(module => module.ConfiguredClockSpeedMHz ?? module.SpeedMHz)
                .Where(speed => speed is > 0)
                .Select(speed => speed!.Value)
                .Distinct()
                .OrderBy(speed => speed)
                .ToArray();
            if (speeds.Length > 0)
            {
                parts.Add(string.Join(" / ", speeds.Select(speed => speed + " MHz")));
            }

            parts.Add(modules.Count + " 条");
            return new InventoryDisplayRow("内存", string.Join(" · ", parts));
        }

        private static InventoryDisplayRow BuildGpuRow(HardwareInventorySnapshot snapshot)
        {
            // Gate 0.2：Basic Render Driver 不进入用户可见 GPU 列表。
            var gpus = GpuDisplayPolicy.SelectUserFacingGpus(snapshot.Gpus);
            if (gpus.Count == 0)
            {
                return new InventoryDisplayRow("显卡", NotDetected);
            }

            var entries = new List<string>();
            foreach (var gpu in gpus)
            {
                var name = FirstKnown(gpu.Name, gpu.Vendor) ?? NotDetected;
                entries.Add(GpuDisplayPolicy.ShouldReportDedicatedVram(gpu.DedicatedVideoMemoryBytes)
                    ? name + " (" + FormatBytes(gpu.DedicatedVideoMemoryBytes!.Value) + ")"
                    : name);
            }

            return new InventoryDisplayRow("显卡", string.Join(" / ", entries));
        }

        private static InventoryDisplayRow BuildMonitorRow(HardwareInventorySnapshot snapshot)
        {
            var monitors = snapshot.Monitors;
            if (monitors.Count == 0)
            {
                return new InventoryDisplayRow("显示器", NotDetected);
            }

            var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary == true)
                ?? monitors[0];
            var parts = new List<string>
            {
                FirstKnown(primary.FriendlyName, primary.EdidIdentity, primary.ProductCode) ?? NotDetected,
            };

            if (!string.IsNullOrWhiteSpace(primary.CurrentResolution))
            {
                parts.Add(primary.CurrentResolution!.Trim());
            }

            if (primary.CurrentRefreshRateHz is > 0)
            {
                parts.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{primary.CurrentRefreshRateHz.Value:0.#} Hz"));
            }

            if (primary.DiagonalInches is > 0)
            {
                parts.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"约 {primary.DiagonalInches.Value:0.#} 英寸"));
            }

            if (primary.IsPrimary == true)
            {
                parts.Add("主屏");
            }

            var extra = monitors.Count - 1;
            var value = string.Join(" · ", parts);
            if (extra > 0)
            {
                value += $" · 另有 {extra} 台显示器";
            }

            return new InventoryDisplayRow("显示器", value);
        }

        private static InventoryDisplayRow BuildStorageRow(HardwareInventorySnapshot snapshot)
        {
            var disks = snapshot.Disks;
            if (disks.Count == 0)
            {
                return new InventoryDisplayRow("硬盘", NotDetected);
            }

            var entries = new List<string>();
            foreach (var disk in disks)
            {
                var name = FirstKnown(disk.FriendlyName, disk.Model) ?? NotDetected;
                var capacity = disk.SizeBytes.HasValue ? FormatBytes(disk.SizeBytes.Value) : null;
                entries.Add(capacity is null ? name : name + " " + capacity);
            }

            return new InventoryDisplayRow("硬盘", string.Join(" / ", entries));
        }

        /// <summary>声卡行：优先 hardware audio controller；没有控制器时才退回播放端点摘要。</summary>
        private static InventoryDisplayRow? BuildAudioRow(HardwareInventorySnapshot snapshot)
        {
            var controllers = (snapshot.AudioControllers ?? Array.Empty<AudioControllerInfo>())
                .Where(controller => !string.IsNullOrWhiteSpace(controller.Name))
                .Select(controller => controller.Name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (controllers.Length > 0)
            {
                return new InventoryDisplayRow("声卡", Summarize(controllers));
            }

            var playback = snapshot.AudioDevices
                .Where(device => device.Direction == AudioEndpointDirection.Playback)
                .Select(device => device.FriendlyName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (playback.Length == 0)
            {
                return null;
            }

            return new InventoryDisplayRow("声卡", Summarize(playback));
        }

        /// <summary>网卡行：Dashboard 只显示物理适配器；虚拟适配器只在详情页保留。</summary>
        private static InventoryDisplayRow? BuildNetworkRow(HardwareInventorySnapshot snapshot)
        {
            var physical = snapshot.NetworkAdapters
                .Where(adapter => !adapter.IsVirtual)
                .Select(adapter => FirstKnown(adapter.Name, adapter.Description))
                .Where(name => name is not null)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (physical.Length == 0)
            {
                return null;
            }

            return new InventoryDisplayRow("网卡", Summarize(physical));
        }

        private static string Summarize(IReadOnlyList<string> entries)
        {
            var shown = entries.Take(MaxSummaryEntries).ToArray();
            var value = string.Join(" / ", shown);
            return entries.Count > MaxSummaryEntries
                ? value + $" 等 {entries.Count} 个设备"
                : value;
        }

        private static string? FirstKnown(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        private static string? JoinParts(params string?[] values)
        {
            var parts = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return parts.Length > 0 ? string.Join(" · ", parts) : null;
        }

        internal static string FormatBytes(ulong bytes) =>
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{bytes / 1073741824d:0.#} GB");

        private const string NotDetected = "未检测到";
    }
}
