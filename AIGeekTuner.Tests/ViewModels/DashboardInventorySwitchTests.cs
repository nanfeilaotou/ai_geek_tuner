using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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
    /// V2-M4.5C.1 Gate A：Dashboard 启动重叠 bug 的确定性回归测试。
    ///
    /// 根因：HasDashboardDetails / HasInventoryDetail 曾是普通 auto-property，
    /// inventory 完成时不触发 PropertyChanged → Dashboard 兜底 legacy Grid 的
    /// DataTrigger 永不折叠，旧英文行（CPU/Memory/Motherboard/...）与新 rich
    /// rows 在同一 Grid 单元内重叠；切页重新绑定后才恢复。修复：两 flag 改为
    /// SetProperty，且 ApplyInventory 先完整构建新 presentation 再一次性切换。
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

        [Fact]
        public async Task DelayedInventory_Completion_SwitchesAtomicallyWithoutLegacyRows()
        {
            var inventory = new DelayedInventoryService();
            var vm = new HardwareInfoViewModel(
                new StubDetectionService(),
                new StubSensorService(),
                hardwareInventoryService: inventory);

            // 1) 初始：inventory 未完成 → legacy 兜底状态（rich rows 为空、flag false）。
            Assert.False(vm.HasDashboardDetails);
            Assert.Empty(vm.DashboardDetailRows);
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
    }
}
