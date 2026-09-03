using System;

namespace AIGeekTuner.Models.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5A：Windows 静态硬件 Inventory（数据底座）。
    ///
    /// 架构不变量（Gate B）：Static Hardware Inventory ≠ Realtime Telemetry。
    /// 本 inventory 完全不依赖 AIDA64/HWiNFO/LHM 运行；这些 provider 以后只做
    /// telemetry enrichment。Monitor/Audio/Network 不进 CanonicalMetric。
    ///
    /// Gate M：任何单类检测失败 → 对应 section 为 null/空，绝不拖垮整个 snapshot；
    /// 缺失字段一律 null（不填 "Unknown" 污染数据层），由 UI 决定不显示。
    /// Gate L：每个 section 标记来源（Wmi/DXGI/CoreAudio/...），UI 本轮不显示 badge。
    /// </summary>
    public enum InventorySource
    {
        Wmi,
        WmiMonitor,
        GdiDisplay,
        DXGI,
        CoreAudio,
        WindowsNetwork,
    }

    public enum AudioEndpointDirection
    {
        Playback,
        Capture,
    }

    public sealed record HardwareInventorySnapshot(
        CpuInventoryInfo? Cpu,
        MotherboardInventoryInfo? Motherboard,
        BiosInventoryInfo? Bios,
        OsInventoryInfo? Os,
        IReadOnlyList<MemoryModuleInfo> MemoryModules,
        IReadOnlyList<GpuInventoryInfo> Gpus,
        IReadOnlyList<StorageDiskInventoryInfo> Disks,
        IReadOnlyList<MonitorInventoryInfo> Monitors,
        IReadOnlyList<AudioDeviceInfo> AudioDevices,
        IReadOnlyList<NetworkAdapterInventoryInfo> NetworkAdapters,
        DateTimeOffset CollectedAtUtc,
        // V2-M4.5B：hardware audio controller supplement（Win32_SoundDevice）。
        // CoreAudio endpoints 是端点不是硬件；Dashboard/详情页优先展示硬件控制器。
        IReadOnlyList<AudioControllerInfo>? AudioControllers = null,
        // V2-M4.5C.1 Gate I：电池（Win32_Battery + root\WMI Battery*，无电池为 null）。
        BatteryInfo? Battery = null);

    public sealed record CpuInventoryInfo(
        string? Name,
        string? Manufacturer,
        string? Architecture,
        uint? PhysicalCores,
        uint? LogicalCores,
        uint? MaxClockSpeedMHz,
        uint? BaseClockSpeedMHz,
        uint? L2CacheSizeKB,
        uint? L3CacheSizeKB,
        bool? VirtualizationFirmwareEnabled,
        InventorySource Source);

    public sealed record MotherboardInventoryInfo(
        string? Manufacturer,
        string? Product,
        string? Version,
        string? SerialNumber,
        InventorySource Source);

    public sealed record BiosInventoryInfo(
        string? Manufacturer,
        string? SmbiosBiosVersion,
        DateTimeOffset? ReleaseDateUtc,
        string? SmbiosVersion,
        InventorySource Source);

    public sealed record OsInventoryInfo(
        string? Name,
        string? Version,
        string? Architecture,
        string? ComputerName,
        DateTimeOffset? LastBootUtc,
        InventorySource Source);

    /// <summary>每条 DIMM 独立 module identity（Gate E），绝不汇总成一条字符串。</summary>
    public sealed record MemoryModuleInfo(
        string? DeviceLocator,
        string? BankLabel,
        ulong? CapacityBytes,
        string? Manufacturer,
        string? PartNumber,
        string? SerialNumber,
        uint? SpeedMHz,
        uint? ConfiguredClockSpeedMHz,
        string? FormFactor,
        uint? DataWidthBits,
        uint? TotalWidthBits,
        InventorySource Source,
        // V2-M4.5C：SMBIOSMemoryType（SMBIOS 官方编码，34=DDR5/26=DDR4/24=DDR3）。
        // Win32_PhysicalMemory.MemoryType 遗留字段不可靠（真机返回 0），不使用。
        uint? SmbiosMemoryType = null);

    public sealed record GpuInventoryInfo(
        string? Name,
        string? Vendor,
        uint? VendorId,
        uint? DeviceId,
        string? PnpDeviceId,
        string? DriverVersion,
        DateTimeOffset? DriverDateUtc,
        ulong? DedicatedVideoMemoryBytes,
        ulong? SharedSystemMemoryBytes,
        InventorySource Source);

    public sealed record StoragePartitionInfo(
        string? DriveLetter,
        string? FileSystem,
        string? Label,
        ulong? SizeBytes,
        ulong? FreeSpaceBytes);

    /// <summary>
    /// V2-M4.5C.1 Gate I：电池信息（容量单位 mWh，电压 mV，与 WMI 原始口径一致）。
    /// 缺失来源一律 null；Health/Wear 只有设计+满充都 &gt; 0 才有值。
    /// </summary>
    public sealed record BatteryInfo(
        string? Name,
        bool PowerOnline,
        bool Charging,
        bool Discharging,
        uint? ChargePercent,
        uint? DesignCapacityMWh,
        uint? FullChargeCapacityMWh,
        uint? RemainingCapacityMWh,
        uint? VoltageMillivolts,
        uint? ChargeRateMilliwatts,
        uint? DischargeRateMilliwatts,
        double? HealthPercent,
        double? WearPercent,
        InventorySource Source);

    public sealed record StorageDiskInventoryInfo(
        string? Model,
        string? FriendlyName,
        string? SerialNumber,
        string? FirmwareVersion,
        ulong? SizeBytes,
        string? BusType,
        string? MediaType,
        string? HealthStatus,
        uint? DiskNumber,
        IReadOnlyList<StoragePartitionInfo> Partitions,
        InventorySource Source);

    public sealed record MonitorInventoryInfo(
        string? FriendlyName,
        string? ManufacturerCode,
        string? ProductCode,
        string? SerialNumber,
        uint? YearOfManufacture,
        uint? PhysicalWidthCm,
        uint? PhysicalHeightCm,
        double? DiagonalInches,
        string? CurrentResolution,
        double? CurrentRefreshRateHz,
        bool? IsPrimary,
        string? EdidIdentity,
        InventorySource Source);

    /// <summary>
    /// V2-M4.5B：hardware audio controller（Win32_SoundDevice）。
    /// 与 CoreAudio endpoint 分层：controller 是硬件，endpoint 是系统端点。
    /// </summary>
    public sealed record AudioControllerInfo(
        string? Name,
        string? Manufacturer,
        string? Status,
        string? PnpDeviceId,
        InventorySource Source);

    public sealed record AudioDeviceInfo(
        string? FriendlyName,
        string? DeviceId,
        AudioEndpointDirection Direction,
        string? State,
        bool IsDefault,
        InventorySource Source);

    public sealed record NetworkAdapterInventoryInfo(
        string? Name,
        string? Description,
        string? InterfaceType,
        string? OperationalStatus,
        ulong? LinkSpeedBps,
        string? MacAddress,
        IReadOnlyList<string> IPv4Addresses,
        IReadOnlyList<string> IPv6Addresses,
        bool? DhcpEnabled,
        IReadOnlyList<string> Gateways,
        IReadOnlyList<string> DnsServers,
        bool IsVirtual,
        InventorySource Source);
}
