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

        public async Task<HardwareInventorySnapshot> CollectAsync(
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Collect(cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        private HardwareInventorySnapshot Collect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cpu = Section("Cpu", () => SystemInventoryMappers.MapCpu(
                _wmi.Query("Win32_Processor"), _wmi.Query("Win32_CacheMemory")));
            var motherboard = Section("Motherboard", () => SystemInventoryMappers.MapMotherboard(
                _wmi.Query("Win32_BaseBoard")));
            var bios = Section("Bios", () => SystemInventoryMappers.MapBios(
                _wmi.Query("Win32_BIOS")));
            var os = Section("Os", () => SystemInventoryMappers.MapOs(
                _wmi.Query("Win32_OperatingSystem"),
                Environment.MachineName));
            var memory = ListSection("Memory", () => MemoryInventoryMapper.Map(
                _wmi.Query("Win32_PhysicalMemory")));
            var gpus = ListSection("Gpu", () => GpuInventoryMapper.Map(
                _gpuAdapters.GetAdapters(), _wmi.Query("Win32_VideoController")));
            var disks = ListSection("Storage", () => StorageInventoryMapper.Map(
                _wmi.Query("MSFT_PhysicalDisk", StorageScope),
                _wmi.Query("MSFT_Partition", StorageScope),
                _wmi.Query("MSFT_Volume", StorageScope)));
            var monitors = ListSection("Monitor", CollectMonitors);
            var audio = ListSection("Audio", () => AudioInventoryMapper.Map(
                _audioEndpoints.GetEndpoints()));
            var network = ListSection("Network", () => NetworkInventoryMapper.Map(
                _networkAdapters.GetAdapters()));

            return new HardwareInventorySnapshot(
                Cpu: cpu,
                Motherboard: motherboard,
                Bios: bios,
                Os: os,
                MemoryModules: memory,
                Gpus: gpus,
                Disks: disks,
                Monitors: monitors,
                AudioDevices: audio,
                NetworkAdapters: network,
                CollectedAtUtc: DateTimeOffset.UtcNow);
        }

        private IReadOnlyList<MonitorInventoryInfo> CollectMonitors()
        {
            var monitorIds = _wmi.Query("WmiMonitorID", MonitorScope);
            if (monitorIds.Count == 0)
            {
                return Array.Empty<MonitorInventoryInfo>();
            }

            var displayParams = _wmi.Query("WmiMonitorBasicDisplayParams", MonitorScope);
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