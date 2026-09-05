using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5A：静态硬件 Inventory 采集入口。
    ///
    /// Gate M：每个 category 独立 best-effort——单类失败只留痕（ExceptionLogWriter）
    /// 并降级为 null/空 section，绝不让 Hardware detection 整体失败；
    /// 缺失字段保持 null，不填 "Unknown"。
    /// Gate B：完全不依赖 AIDA64/HWiNFO/LHM。
    /// </summary>
    public interface IHardwareInventoryService
    {
        Task<HardwareInventorySnapshot> CollectAsync(CancellationToken cancellationToken = default);
    }

    public sealed class HardwareInventoryService : IHardwareInventoryService
    {
        private const string StorageScope = @"root\Microsoft\Windows\Storage";
        private const string MonitorScope = @"root\wmi";

        private readonly IWmiInventorySource _wmi;
        private readonly IGpuAdapterSource _gpuAdapters;
        private readonly IAudioEndpointSource _audioEndpoints;
        private readonly IDisplayModeSource _displayModes;
        private readonly INetworkAdapterSource _networkAdapters;
        private readonly object _collectionGate = new();
        private Task<HardwareInventorySnapshot>? _collectionTask;

        private static readonly string[] CpuProperties =
        ["Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors",
            "MaxClockSpeed", "Architecture", "VirtualizationFirmwareEnabled"];
        private static readonly string[] CacheProperties = ["Level", "InstalledSize"];
        private static readonly string[] MotherboardProperties = ["Manufacturer", "Product", "Version", "SerialNumber"];
        private static readonly string[] BiosProperties =
            ["Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate", "SMBIOSMajorVersion", "SMBIOSMinorVersion"];
        private static readonly string[] OsProperties = ["Caption", "Version", "OSArchitecture", "LastBootUpTime"];
        private static readonly string[] MemoryProperties =
        ["Capacity", "Manufacturer", "DeviceLocator", "PartNumber", "BankLabel", "SerialNumber",
            "Speed", "ConfiguredClockSpeed", "FormFactor", "DataWidth", "TotalWidth", "SMBIOSMemoryType"];
        private static readonly string[] VideoProperties =
            ["Name", "AdapterCompatibility", "PNPDeviceID", "DriverVersion", "DriverDate"];
        private static readonly string[] PhysicalDiskProperties =
        ["DeviceId", "FriendlyName", "Model", "SerialNumber", "FirmwareVersion", "Size",
            "BusType", "MediaType", "HealthStatus"];
        private static readonly string[] PartitionProperties = ["DiskNumber", "DriveLetter", "Size"];
        private static readonly string[] VolumeProperties = ["DriveLetter", "FileSystem", "FileSystemLabel", "Size", "SizeRemaining"];
        private static readonly string[] MonitorIdProperties =
        ["InstanceName", "ManufacturerName", "ProductCodeId", "SerialNumberId", "UserFriendlyName", "YearOfManufacture"];
        private static readonly string[] MonitorParamsProperties =
            ["InstanceName", "MaxHorizontalImageSize", "MaxVerticalImageSize"];
        private static readonly string[] SoundProperties = ["Name", "Manufacturer", "Status", "PNPDeviceID"];
        private static readonly string[] BatteryStaticProperties = ["DesignedCapacity"];
        private static readonly string[] BatteryFullProperties = ["FullChargedCapacity"];
        private static readonly string[] BatteryStatusProperties =
            ["RemainingCapacity", "Voltage", "ChargeRate", "DischargeRate", "PowerOnline", "Charging", "Discharging"];
        private static readonly string[] BatteryProperties = ["Name", "EstimatedChargeRemaining", "BatteryStatus", "DesignVoltage"];

        public HardwareInventoryService(
            IWmiInventorySource wmi,
            IGpuAdapterSource gpuAdapters,
            IAudioEndpointSource audioEndpoints,
            IDisplayModeSource displayModes,
            INetworkAdapterSource networkAdapters)
        {
            _wmi = wmi ?? throw new ArgumentNullException(nameof(wmi));
            _gpuAdapters = gpuAdapters ?? throw new ArgumentNullException(nameof(gpuAdapters));
            _audioEndpoints = audioEndpoints ?? throw new ArgumentNullException(nameof(audioEndpoints));
            _displayModes = displayModes ?? throw new ArgumentNullException(nameof(displayModes));
            _networkAdapters = networkAdapters ?? throw new ArgumentNullException(nameof(networkAdapters));
        }

        public Task<HardwareInventorySnapshot> CollectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<HardwareInventorySnapshot> task;
            lock (_collectionGate)
            {
                _collectionTask ??= CollectParallelAsync();
                task = _collectionTask;
            }

            // Cancellation cancels only this caller's wait.  The shared startup
            // snapshot continues so Dashboard and Hardware detail cannot diverge.
            return cancellationToken.CanBeCanceled
                ? task.WaitAsync(cancellationToken)
                : task;
        }

        private async Task<HardwareInventorySnapshot> CollectParallelAsync()
        {
            // Each task owns its WMI/COM source call.  There is no shared
            // ManagementObjectSearcher, DXGI enumerator, or CoreAudio object.
            var cpuTask = Task.Run(() => Section("Cpu", () => SystemInventoryMappers.MapCpu(
                Query("Win32_Processor", null, CpuProperties),
                Query("Win32_CacheMemory", null, CacheProperties))));
            var motherboardTask = Task.Run(() => Section("Motherboard", () => SystemInventoryMappers.MapMotherboard(
                Query("Win32_BaseBoard", null, MotherboardProperties))));
            var biosTask = Task.Run(() => Section("Bios", () => SystemInventoryMappers.MapBios(
                Query("Win32_BIOS", null, BiosProperties))));
            var osTask = Task.Run(() => Section("Os", () => SystemInventoryMappers.MapOs(
                Query("Win32_OperatingSystem", null, OsProperties), Environment.MachineName)));
            var memoryTask = Task.Run(() => ListSection("Memory", () => MemoryInventoryMapper.Map(
                Query("Win32_PhysicalMemory", null, MemoryProperties))));
            var gpuTask = Task.Run(() => ListSection("Gpu", () => GpuInventoryMapper.Map(
                _gpuAdapters.GetAdapters(), Query("Win32_VideoController", null, VideoProperties))));
            var storageTask = Task.Run(() => ListSection("Storage", () => StorageInventoryMapper.Map(
                Query("MSFT_PhysicalDisk", StorageScope, PhysicalDiskProperties),
                Query("MSFT_Partition", StorageScope, PartitionProperties),
                Query("MSFT_Volume", StorageScope, VolumeProperties))));
            var monitorTask = Task.Run(() => ListSection("Monitor", CollectMonitors));
            var audioTask = Task.Run(() => ListSection("Audio", () => AudioInventoryMapper.Map(
                _audioEndpoints.GetEndpoints())));
            var audioControllersTask = Task.Run(() => ListSection("AudioController", () =>
                SystemInventoryMappers.MapAudioControllers(Query("Win32_SoundDevice", null, SoundProperties))));
            var networkTask = Task.Run(() => ListSection("Network", () => NetworkInventoryMapper.Map(
                _networkAdapters.GetAdapters())));
            var batteryTask = Task.Run(() => Section("Battery", () => BatteryInventoryMapper.Map(
                Query("BatteryStaticData", MonitorScope, BatteryStaticProperties),
                Query("BatteryFullChargedCapacity", MonitorScope, BatteryFullProperties),
                Query("BatteryStatus", MonitorScope, BatteryStatusProperties),
                Query("Win32_Battery", null, BatteryProperties))));

            await Task.WhenAll(
                cpuTask, motherboardTask, biosTask, osTask, memoryTask, gpuTask,
                storageTask, monitorTask, audioTask, audioControllersTask, networkTask,
                batteryTask).ConfigureAwait(false);

            return new HardwareInventorySnapshot(
                Cpu: cpuTask.Result,
                Motherboard: motherboardTask.Result,
                Bios: biosTask.Result,
                Os: osTask.Result,
                MemoryModules: memoryTask.Result,
                Gpus: gpuTask.Result,
                Disks: storageTask.Result,
                Monitors: monitorTask.Result,
                AudioDevices: audioTask.Result,
                NetworkAdapters: networkTask.Result,
                CollectedAtUtc: DateTimeOffset.UtcNow,
                AudioControllers: audioControllersTask.Result,
                Battery: batteryTask.Result);
        }

        private IReadOnlyList<MonitorInventoryInfo> CollectMonitors()
        {
            var monitorIds = Query("WmiMonitorID", MonitorScope, MonitorIdProperties);
            if (monitorIds.Count == 0)
            {
                return Array.Empty<MonitorInventoryInfo>();
            }

            var displayParams = Query("WmiMonitorBasicDisplayParams", MonitorScope, MonitorParamsProperties);
            var rawEdids = new List<MonitorInventoryMapper.RawEdidEntry>(monitorIds.Count);
            foreach (var row in monitorIds)
            {
                var instanceName = row.String("InstanceName");
                if (instanceName is null)
                {
                    continue;
                }

                var edid = _wmi.GetMonitorEdid(instanceName);
                if (edid is not null)
                {
                    rawEdids.Add(new MonitorInventoryMapper.RawEdidEntry(instanceName, edid));
                }
            }

            return MonitorInventoryMapper.Map(
                monitorIds, displayParams, rawEdids, _displayModes.GetModes());
        }

        private IReadOnlyList<IInventoryRow> Query(
            string wmiClass,
            string? scope,
            IReadOnlyCollection<string> properties)
        {
            return _wmi is IProjectedWmiInventorySource projected
                ? projected.QueryProjected(wmiClass, scope, properties)
                : _wmi.Query(wmiClass, scope);
        }

        private T? Section<T>(string category, Func<T?> build) where T : class
        {
            try
            {
                return build();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, $"Inventory/{category}");
                return null;
            }
        }

        private IReadOnlyList<T> ListSection<T>(string category, Func<IReadOnlyList<T>> build)
        {
            try
            {
                return build();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, $"Inventory/{category}");
                return Array.Empty<T>();
            }
        }
    }
}
