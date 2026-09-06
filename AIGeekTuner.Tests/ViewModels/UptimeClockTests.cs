using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AIGeekTuner.Models;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory.Presentation;
using AIGeekTuner.ViewModels;
using AIGeekTuner.Views;
using Xunit;

namespace AIGeekTuner.Tests.ViewModels;

/// <summary>
/// M5.2C：仪表盘运行时间秒级刷新契约 —— 格式含秒、逐秒更新、
/// 时钟只做字符串计算（不触发硬件采集）、页面生命周期正确启停。
/// </summary>
[Collection("WpfSmoke")]
public sealed class UptimeClockTests
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

    private sealed class CountingInventoryService : IHardwareInventoryService
    {
        private int _calls;
        public int Calls => _calls;

        public Task<HardwareInventorySnapshot> CollectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(CreateSnapshot());
        }
    }

    // ---- 11) 格式化含秒 ----

    [Theory]
    [InlineData(0, 0, 0, 5, "5 秒")]
    [InlineData(0, 0, 3, 8, "3 分 08 秒")]
    [InlineData(0, 19, 28, 14, "19 小时 28 分 14 秒")]
    [InlineData(7, 6, 3, 8, "7 天 6 小时 3 分 08 秒")]
    public void FormatUptime_IncludesSeconds(int days, int hours, int minutes, int seconds, string expected)
    {
        var uptime = new TimeSpan(days, hours, minutes, seconds);
        Assert.Equal(expected, HardwareInventoryDetailPresenter.FormatUptime(uptime));
    }

    [Fact]
    public void FormatUptime_NegativeIsEmDash()
    {
        Assert.Equal("—", HardwareInventoryDetailPresenter.FormatUptime(TimeSpan.FromSeconds(-1)));
    }

    // ---- 12) 每秒刷新 / 13) 不触发硬件采集 ----

    [Fact]
    public void UptimeClock_UpdatesDisplayEverySecondWithoutInventoryRefresh()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var inventory = new CountingInventoryService();
                var vm = new HardwareInfoViewModel(
                    new StubDetectionService(),
                    new StubSensorService(),
                    hardwareInventoryService: inventory);

                WaitForUptimeReady(vm);

                vm.StartUptimeClock();
                try
                {
                    var first = vm.UptimeDisplay;
                    Assert.Contains("秒", first);

                    var stopwatch = Stopwatch.StartNew();
                    while (stopwatch.Elapsed < TimeSpan.FromSeconds(4)
                           && vm.UptimeDisplay == first)
                    {
                        Pump();
                        Thread.Sleep(50);
                    }

                    Assert.NotEqual(first, vm.UptimeDisplay);
                    // 时钟只算时间字符串：除构造时那一次外不得重新采集。
                    Assert.Equal(1, inventory.Calls);
                }
                finally
                {
                    vm.StopUptimeClock();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "uptime tick test 超时");
        Assert.Null(failure);
    }

    // ---- 14) 生命周期清理 + Dashboard 页面启停 ----

    [Fact]
    public void UptimeClock_StopClearsTimerAndCanBeRestarted()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var inventory = new CountingInventoryService();
                var vm = new HardwareInfoViewModel(
                    new StubDetectionService(),
                    new StubSensorService(),
                    hardwareInventoryService: inventory);
                WaitForUptimeReady(vm);

                vm.StartUptimeClock();
                Assert.NotNull(GetTimerField(vm));

                vm.StopUptimeClock();
                Assert.Null(GetTimerField(vm));

                // 重复 Stop 是幂等的；重新 Start 正常工作（重复 Start 不叠加计时器）。
                vm.StopUptimeClock();
                vm.StartUptimeClock();
                vm.StartUptimeClock();
                var timer = GetTimerField(vm);
                Assert.NotNull(timer);
                vm.StopUptimeClock();
                Assert.Null(GetTimerField(vm));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "uptime lifecycle test 超时");
        Assert.Null(failure);
    }

    [Fact]
    public void DashboardPage_StartsClockOnLoadAndStopsOnUnload()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var inventory = new CountingInventoryService();
                var hardware = new HardwareInfoViewModel(
                    new StubDetectionService(),
                    new StubSensorService(),
                    hardwareInventoryService: inventory);
                var page = new Dashboard { DataContext = new DashboardViewModel(hardware) };

                // 直接驱动 routed event，验证页面 Loaded/Unloaded 与时钟启停的接线。
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.NotNull(GetTimerField(hardware));

                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.Null(GetTimerField(hardware));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "dashboard uptime lifecycle test 超时");
        Assert.Null(failure);
    }

    private static void WaitForUptimeReady(HardwareInfoViewModel vm)
    {
        var stopwatch = Stopwatch.StartNew();
        while (vm.UptimeDisplay == "--" && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(30);
        }

        Assert.NotEqual("--", vm.UptimeDisplay);
    }

    private static object? GetTimerField(HardwareInfoViewModel vm)
    {
        var field = typeof(HardwareInfoViewModel).GetField(
            "_uptimeTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(vm);
    }

    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            Pump();
            Thread.Sleep(30);
        }
    }

    private static HardwareInventorySnapshot CreateSnapshot() => new(
        Cpu: new CpuInventoryInfo("Test CPU", "GenuineIntel", "x64", 4, 8,
            2000, 2000, null, null, null, InventorySource.Wmi),
        Motherboard: null,
        Bios: null,
        Os: new OsInventoryInfo(
            "Microsoft Windows 11 Pro", "10.0.22631", "x64", "TEST-PC",
            DateTimeOffset.UtcNow, InventorySource.Wmi),
        MemoryModules: Array.Empty<MemoryModuleInfo>(),
        Gpus: Array.Empty<GpuInventoryInfo>(),
        Disks: Array.Empty<StorageDiskInventoryInfo>(),
        Monitors: Array.Empty<MonitorInventoryInfo>(),
        AudioDevices: Array.Empty<AudioDeviceInfo>(),
        NetworkAdapters: Array.Empty<NetworkAdapterInventoryInfo>(),
        CollectedAtUtc: DateTimeOffset.UtcNow);
}