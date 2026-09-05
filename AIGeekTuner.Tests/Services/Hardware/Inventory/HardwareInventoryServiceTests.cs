using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
using System.Threading;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>Gate N：部分失败隔离——单类检测失败不拖垮整个 snapshot。</summary>
    public sealed class HardwareInventoryServiceTests
    {
        private sealed class FakeWmiSource : IWmiInventorySource
        {
            public Dictionary<string, IReadOnlyList<IInventoryRow>> Tables { get; } = new();
            public HashSet<string> FailingClasses { get; } = new();

            public IReadOnlyList<IInventoryRow> Query(string wmiClass, string? scope = null)
            {
                if (FailingClasses.Contains(wmiClass))
                {
                    throw new InvalidOperationException("WMI exploded for " + wmiClass);
                }

                return Tables.TryGetValue(wmiClass, out var rows) ? rows : Array.Empty<IInventoryRow>();
            }

            public byte[]? GetMonitorEdid(string instanceName) => null;
        }

        private sealed class ProjectingWmiSource : IWmiInventorySource, IProjectedWmiInventorySource
        {
            public List<(string ClassName, string? Scope, IReadOnlyCollection<string> Properties)> Queries { get; } = [];

            public IReadOnlyList<IInventoryRow> Query(string wmiClass, string? scope = null) => [];

            public IReadOnlyList<IInventoryRow> QueryProjected(
                string wmiClass,
                string? scope,
                IReadOnlyCollection<string> requiredProperties)
            {
                lock (Queries)
                {
                    Queries.Add((wmiClass, scope, requiredProperties.ToArray()));
                }
                return [];
            }

            public byte[]? GetMonitorEdid(string instanceName) => null;
        }

        private sealed class ParallelWmiSource : IWmiInventorySource
        {
            private int _active;
            private int _maxConcurrency;

            public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

            public IReadOnlyList<IInventoryRow> Query(string wmiClass, string? scope = null)
            {
                var active = Interlocked.Increment(ref _active);
                while (true)
                {
                    var currentMax = Volatile.Read(ref _maxConcurrency);
                    if (active <= currentMax
                        || Interlocked.CompareExchange(ref _maxConcurrency, active, currentMax) == currentMax)
                    {
                        break;
                    }
                }

                Thread.Sleep(30);
                Interlocked.Decrement(ref _active);
                return [];
            }

            public byte[]? GetMonitorEdid(string instanceName) => null;
        }

        private sealed class FailingGpuSource : IGpuAdapterSource
        {
            public IReadOnlyList<GpuInventoryMapper.AdapterDescriptor> GetAdapters() =>
                throw new InvalidOperationException("DXGI exploded");
        }

        private sealed class FakeGpuSource : IGpuAdapterSource
        {
            public IReadOnlyList<GpuInventoryMapper.AdapterDescriptor> GetAdapters() =>
            [
                new("NVIDIA GeForce RTX 4080", 0x10DE, 0x2704, 12884901888ul, 0),
            ];
        }

        private sealed class FailingAudioSource : IAudioEndpointSource
        {
            public IReadOnlyList<AudioInventoryMapper.EndpointDescriptor> GetEndpoints() =>
                throw new InvalidOperationException("CoreAudio exploded");
        }

        private sealed class FakeAudioSource : IAudioEndpointSource
        {
            public IReadOnlyList<AudioInventoryMapper.EndpointDescriptor> GetEndpoints() =>
            [
                new("ep-1", "扬声器", AudioEndpointDirection.Playback, "Active", true),
            ];
        }

        private sealed class FakeDisplaySource : IDisplayModeSource
        {
            public IReadOnlyList<MonitorInventoryMapper.DisplayModeDescriptor> GetModes() => [];
        }

        private sealed class FakeNetworkSource : INetworkAdapterSource
        {
            public IReadOnlyList<NetworkInventoryMapper.AdapterDescriptor> GetAdapters() =>
            [
                new("以太网", "Realtek Gaming 2.5GbE", "Ethernet", "Up", 1_000_000_000,
                    "AA-BB-CC-DD-EE-FF", ["192.168.1.10"], [], true, ["192.168.1.1"], []),
            ];
        }

        private static DictionaryInventoryRow Row(params (string Key, object? Value)[] values)
        {
            var dictionary = new Dictionary<string, object?>();
            foreach (var (key, value) in values)
            {
                dictionary[key] = value;
            }

            return new DictionaryInventoryRow(dictionary);
        }

        [Fact]
        public async Task SingleCategoryFailure_DoesNotKillSnapshot()
        {
            var wmi = new FakeWmiSource();
            wmi.FailingClasses.Add("Win32_Processor");       // CPU 检测失败
            wmi.Tables["Win32_PhysicalMemory"] =
            [
                Row(("DeviceLocator", "DIMM0"), ("Capacity", (object)17179869184ul)),
            ];
            wmi.Tables["Win32_BaseBoard"] = [Row(("Manufacturer", "ASUSTeK"), ("Product", "Z790"))];

            var service = new HardwareInventoryService(
                wmi,
                new FailingGpuSource(),       // DXGI 失败
                new FailingAudioSource(),     // CoreAudio 失败
                new FakeDisplaySource(),
                new FakeNetworkSource());

            var snapshot = await service.CollectAsync();

            Assert.Null(snapshot.Cpu);                        // 失败 section → null
            Assert.Empty(snapshot.Gpus);
            Assert.Empty(snapshot.AudioDevices);
            // 其余 section 不受影响。
            Assert.NotNull(snapshot.Motherboard);
            Assert.Equal("ASUSTeK", snapshot.Motherboard!.Manufacturer);
            Assert.Single(snapshot.MemoryModules);
            Assert.Equal(17179869184ul, snapshot.MemoryModules[0].CapacityBytes);
            Assert.NotNull(snapshot.NetworkAdapters);
            Assert.Single(snapshot.NetworkAdapters);
        }

        [Fact]
        public async Task FullSuccess_SnapshotContainsAllSections()
        {
            var wmi = new FakeWmiSource();
            wmi.Tables["Win32_Processor"] = [Row(("Name", "Intel(R) Core(TM) Ultra 7"))];
            wmi.Tables["Win32_PhysicalMemory"] =
            [
                Row(("DeviceLocator", "DIMM0"), ("Capacity", (object)17179869184ul)),
            ];
            wmi.Tables["MSFT_PhysicalDisk"] =
            [
                Row(("DeviceId", (object)0ul), ("FriendlyName", "Samsung SSD 990 PRO"), ("MediaType", 4u), ("BusType", 11u)),
            ];

            var service = new HardwareInventoryService(
                wmi, new FakeGpuSource(), new FakeAudioSource(), new FakeDisplaySource(), new FakeNetworkSource());

            var snapshot = await service.CollectAsync();

            Assert.NotNull(snapshot.Cpu);
            Assert.Single(snapshot.Gpus);
            Assert.Single(snapshot.AudioDevices);
            Assert.Single(snapshot.Disks);
            Assert.NotEqual(default, snapshot.CollectedAtUtc);
        }

        [Fact]
        public async Task CollectAsync_IsSingleFlightForStartupSnapshot()
        {
            var service = new HardwareInventoryService(
                new FakeWmiSource(), new FakeGpuSource(), new FakeAudioSource(),
                new FakeDisplaySource(), new FakeNetworkSource());

            var first = service.CollectAsync();
            var second = service.CollectAsync();

            Assert.Same(first, second);
            await first;
        }

        [Fact]
        public async Task IndependentCategories_CollectInParallel()
        {
            var wmi = new ParallelWmiSource();
            var service = new HardwareInventoryService(
                wmi, new FakeGpuSource(), new FakeAudioSource(),
                new FakeDisplaySource(), new FakeNetworkSource());

            await service.CollectAsync();

            Assert.True(wmi.MaxConcurrency > 1, "independent inventory categories should overlap");
        }

        [Fact]
        public async Task RichInventory_UsesMapperProjectionsInsteadOfSelectStar()
        {
            var wmi = new ProjectingWmiSource();
            var service = new HardwareInventoryService(
                wmi, new FakeGpuSource(), new FakeAudioSource(),
                new FakeDisplaySource(), new FakeNetworkSource());

            await service.CollectAsync();

            var processor = Assert.Single(wmi.Queries, query => query.ClassName == "Win32_Processor");
            Assert.Contains("Name", processor.Properties);
            Assert.Contains("NumberOfLogicalProcessors", processor.Properties);
            Assert.DoesNotContain("*", processor.Properties);

            var operatingSystem = Assert.Single(wmi.Queries, query => query.ClassName == "Win32_OperatingSystem");
            Assert.Contains("Caption", operatingSystem.Properties);
            Assert.Contains("LastBootUpTime", operatingSystem.Properties);
            Assert.DoesNotContain("*", operatingSystem.Properties);
        }

        [Fact]
        public void WmiProjectionQuery_IsExplicitAndValidated()
        {
            var query = WmiInventorySource.BuildSelectQuery(
                "Win32_Processor",
                new[] { "Name", "MaxClockSpeed" });

            Assert.Equal("SELECT Name,MaxClockSpeed FROM Win32_Processor", query);
            Assert.DoesNotContain("*", query);
            Assert.Throws<ArgumentException>(() =>
                WmiInventorySource.BuildSelectQuery("Win32_Processor; DROP TABLE X"));
        }
    }
}
