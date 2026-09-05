using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIGeekTuner.Models;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory.Presentation;
using AIGeekTuner.ViewModels;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels
{
    /// <summary>
    /// V2-M5.2A.3：Dashboard 启动只使用 Rich Inventory presentation。
    /// Loading 与 ready 共用同一组中文行，避免 legacy 5-row → rich rows 跳变。
    /// </summary>
    public sealed class DashboardInventorySwitchTests
    {
        private sealed class StubDetectionService : IHardwareDetectionService
        {
            public Task<HardwareInfo> DetectAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new HardwareInfo());
        }

        private sealed class StubSensorService : IHardwareSensorService
        {
            public Task<HardwareSensorSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new HardwareSensorSnapshot());
        }

        /// <summary>模拟真实 WMI 采集耗时：CollectAsync 挂起，直到测试显式放行。</summary>
        private sealed class DelayedInventoryService : IHardwareInventoryService
        {
            private readonly TaskCompletionSource<HardwareInventorySnapshot> _tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<HardwareInventorySnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
                _tcs.Task;

            public void Complete(HardwareInventorySnapshot snapshot) => _tcs.SetResult(snapshot);
        }

        private sealed class DelayedDetectionService : IHardwareDetectionService
        {
            private readonly TaskCompletionSource<HardwareInfo> _tcs =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<HardwareInfo> DetectAsync(CancellationToken cancellationToken = default) => _tcs.Task;

            public void Complete() => _tcs.SetResult(new HardwareInfo());
        }

        [Fact]
        public async Task DelayedInventory_Completion_SwitchesAtomicallyWithoutLegacyRows()
        {
            var inventory = new DelayedInventoryService();
            var vm = new HardwareInfoViewModel(
                new StubDetectionService(),
                new StubSensorService(),
                hardwareInventoryService: inventory);

            // 1) 初始：inventory 未完成 → final Rich row shape 的稳定 loading。
            Assert.False(vm.HasDashboardDetails);
            Assert.Equal(
                new[] { "主板", "处理器", "内存", "显卡", "显示器", "硬盘" },
                vm.DashboardDetailRows.Select(row => row.Label));
            Assert.True(vm.IsInventoryLoading);
            Assert.False(vm.HasInventoryError);
            Assert.False(vm.HasInventoryDetail);
            Assert.Empty(vm.InventorySections);

            var propertyChanges = new List<string>();
            vm.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName ?? string.Empty);

            var rowEvents = new List<NotifyCollectionChangedAction>();
            vm.DashboardDetailRows.CollectionChanged += (_, e) => rowEvents.Add(e.Action);

            // 2) inventory 完成 → 必须发生一次原子 presentation 切换。
            var snapshot = CreateSnapshot();
            inventory.Complete(snapshot);
            await WaitUntilAsync(() => vm.HasDashboardDetails);

            // 3) 最终行与 presenter 输出逐条一致：无 legacy 遗留、无重复条目。
            var expectedRows = DashboardInventoryPresenter.BuildRows(snapshot).ToArray();
            Assert.NotEmpty(expectedRows);
            Assert.Equal(expectedRows.Length, vm.DashboardDetailRows.Count);
            for (var i = 0; i < expectedRows.Length; i++)
            {
                Assert.Equal(expectedRows[i].Label, vm.DashboardDetailRows[i].Label);
                Assert.Equal(expectedRows[i].Value, vm.DashboardDetailRows[i].Value);
            }

            // 4) 切换只发生一次：集合事件恰为 1 次 Reset + N 次 Add，绝无二次 Clear。
            Assert.Equal(1, rowEvents.Count(a => a == NotifyCollectionChangedAction.Reset));
            Assert.Equal(
                expectedRows.Length,
                rowEvents.Count(a => a == NotifyCollectionChangedAction.Add));

            // 5) 根因回归锁：两个 flag 必须真正触发 PropertyChanged。
            Assert.Contains(nameof(HardwareInfoViewModel.HasDashboardDetails), propertyChanges);
            Assert.Contains(nameof(HardwareInfoViewModel.HasInventoryDetail), propertyChanges);

            var expectedSections = HardwareInventoryDetailPresenter.BuildSections(snapshot).ToArray();
            Assert.Equal(expectedSections.Length, vm.InventorySections.Count);
            Assert.True(vm.HasInventoryDetail);

            // 6) 稳定性：再让调度器运行，集合不得二次变化（无 double-add）。
            await Task.Delay(50);
            Assert.Equal(expectedRows.Length, vm.DashboardDetailRows.Count);
            Assert.Equal(expectedSections.Length, vm.InventorySections.Count);
        }

        [Fact]
        public async Task RichInventory_DoesNotWaitForLegacyDetection()
        {
            var inventory = new DelayedInventoryService();
            var detection = new DelayedDetectionService();
            var vm = new HardwareInfoViewModel(detection, new StubSensorService(), hardwareInventoryService: inventory);

            try
            {
                inventory.Complete(CreateSnapshot());
                await WaitUntilAsync(() => vm.HasDashboardDetails);
                Assert.True(vm.IsInventoryLoading == false);
            }
            finally
            {
                detection.Complete();
            }
        }

        [Fact]
        public void LoadingRows_AreTheFinalRichPresentationShape()
        {
            var rows = DashboardInventoryPresenter.BuildLoadingRows();

            Assert.Equal(
                new[] { "主板", "处理器", "内存", "显卡", "显示器", "硬盘" },
                rows.Select(row => row.Label));
            Assert.All(rows, row => Assert.Equal("正在读取…", row.Value));
            Assert.DoesNotContain(rows, row =>
                row.Label is "CPU" or "GPU" or "Memory" or "Motherboard" or "Operating System");
        }

        [Fact]
        public void DashboardXaml_MakesLegacyRowsFailureOnly()
        {
            var path = Path.Combine("AIGeekTuner", "Views", "Dashboard.xaml");
            var xaml = File.ReadAllText(FindRepositoryFile(path));

            Assert.Contains("Hardware.HasInventoryError", xaml);
            Assert.DoesNotContain("Hardware.HasDashboardDetails", xaml);
            Assert.Contains("Failure-only compatibility fallback", xaml);
        }

        private static HardwareInventorySnapshot CreateSnapshot() => new(
            Cpu: new CpuInventoryInfo(
                "Intel Core i9 Test CPU", "GenuineIntel", "x64", 8, 24,
                2200, 2200, null, null, null, InventorySource.Wmi),
            Motherboard: new MotherboardInventoryInfo(
                "ASUSTeK COMPUTER INC.", "G834JZ", null, "SN-TEST", InventorySource.Wmi),
            Bios: null,
            Os: new OsInventoryInfo(
                "Microsoft Windows 11 Pro", "10.0.22631", "x64", "TEST-PC",
                new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero), InventorySource.Wmi),
            MemoryModules: Array.Empty<MemoryModuleInfo>(),
            Gpus: Array.Empty<GpuInventoryInfo>(),
            Disks: Array.Empty<StorageDiskInventoryInfo>(),
            Monitors: Array.Empty<MonitorInventoryInfo>(),
            AudioDevices: Array.Empty<AudioDeviceInfo>(),
            NetworkAdapters: Array.Empty<NetworkAdapterInventoryInfo>(),
            CollectedAtUtc: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("inventory completion was not observed in time");
                }

                await Task.Delay(10);
            }
        }

        private static string FindRepositoryFile(string relativePath)
        {
            var candidate = AppContext.BaseDirectory;
            for (var i = 0; i < 6; i++)
            {
                var probe = Path.GetFullPath(Path.Combine(candidate, relativePath));
                if (File.Exists(probe))
                {
                    return probe;
                }

                candidate = Path.GetDirectoryName(candidate)!;
            }

            throw new FileNotFoundException($"无法定位测试文件 {relativePath}");
        }
    }
}
