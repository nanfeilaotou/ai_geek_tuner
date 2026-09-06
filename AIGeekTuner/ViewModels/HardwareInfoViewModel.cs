using System.Collections.ObjectModel;
using System.Windows.Threading;
using AIGeekTuner.Commands;
using AIGeekTuner.Models;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory.Presentation;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Presentation;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.ViewModels
{
    public sealed class HardwareInfoViewModel : ViewModelBase
    {
        private readonly IHardwareDetectionService _hardwareDetectionService;
        private readonly IHardwareSensorService _hardwareSensorService;
        private readonly IHardwareInventoryService? _hardwareInventoryService;
        private readonly LiveMetricRangeTracker _rangeTracker = new();
        private readonly ITelemetryHub? _telemetryHub;
        private readonly ILiveTelemetrySource? _liveSource;
        private readonly AsyncRelayCommand _refreshSensorsCommand;
        private readonly RelayCommand _resetRangesCommand;
        private IReadOnlyList<Models.Hardware.Inventory.MemoryModuleInfo> _inventoryMemoryModules = [];
        private System.Windows.Threading.DispatcherTimer? _uptimeTimer;
        private DateTimeOffset? _uptimeLastBootUtc;

        private string _deviceModel = "正在读取...";
        private string _systemSummary = "正在读取...";
        private string _runtimeStatus = "硬件检测进行中";
        private string _cpuName = NotDetectedDisplay;
        private string _cpuPhysicalCores = NotDetectedDisplay;
        private string _cpuLogicalProcessors = NotDetectedDisplay;
        private string _cpuMaxClock = NotDetectedDisplay;
        private string _gpuNames = NotDetectedDisplay;
        private string _gpuDetails = NotDetectedDisplay;
        private string _memoryCapacity = NotDetectedDisplay;
        private string _memoryManufacturers = NotDetectedDisplay;
        private string _memorySpeeds = NotDetectedDisplay;
        private string _memoryModuleCount = NotDetectedDisplay;
        private string _motherboard = NotDetectedDisplay;
        private string _motherboardManufacturer = NotDetectedDisplay;
        private string _motherboardProduct = NotDetectedDisplay;
        private string _storageDevices = NotDetectedDisplay;
        private string _operatingSystem = NotDetectedDisplay;
        private string _operatingSystemName = NotDetectedDisplay;
        private string _operatingSystemVersion = NotDetectedDisplay;
        private string _operatingSystemArchitecture = NotDetectedDisplay;
        private string _detectedAt = "--";
        private string _uptimeDisplay = "--";
        private string _lastBootDisplay = "--";
        private string _sensorStatus = "等待读取实时传感器";
        private string _sensorCapturedAt = "--";
        private bool _isLoading = true;
        private bool _hasError;
        private bool _isSensorRefreshing;
        private bool _hasSensorData;
        private bool _hasInitializedSensors;
        private bool _hasDataSources;
        private bool _hasLiveCards;
        private bool _hasDashboardDetails;
        private bool _hasInventoryDetail;
        private bool _isInventoryLoading;
        private bool _hasInventoryError;
        private string _autoRefreshSummary = "自动刷新已关闭";
        private IReadOnlyList<TelemetryDebugRow> _lastDebugRows = [];

        public HardwareInfoViewModel(
            IHardwareDetectionService hardwareDetectionService,
            IHardwareSensorService hardwareSensorService,
            ITelemetryHub? telemetryHub = null,
            ILiveTelemetrySource? liveTelemetrySource = null,
            IHardwareInventoryService? hardwareInventoryService = null)
        {
            _hardwareDetectionService = hardwareDetectionService
                ?? throw new ArgumentNullException(nameof(hardwareDetectionService));
            _hardwareSensorService = hardwareSensorService
                ?? throw new ArgumentNullException(nameof(hardwareSensorService));
            _hardwareInventoryService = hardwareInventoryService;
            _telemetryHub = telemetryHub;
            _resetRangesCommand = new RelayCommand(
                () => _rangeTracker.Reset(),
                () => HasLiveCards);
            if (liveTelemetrySource is not null)
            {
                _liveSource = liveTelemetrySource;
                _liveSource.SnapshotUpdated += s =>
                {
                    // 事件契约：后台线程触发（§15）。范围跟踪自身线程安全，
                    // 可即时记录；显示更新必须调度回 UI 线程改 ObservableCollection。
                    _rangeTracker.Update(s);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher is null || dispatcher.CheckAccess())
                    {
                        ApplyTelemetrySnapshot(s);
                    }
                    else
                    {
                        dispatcher.BeginInvoke(() => ApplyTelemetrySnapshot(s));
                    }
                };
            }
            _refreshSensorsCommand = new AsyncRelayCommand(
                RefreshSensorsAsync,
                () => !IsSensorRefreshing);

            if (_hardwareInventoryService is not null)
            {
                foreach (var row in DashboardInventoryPresenter.BuildLoadingRows())
                {
                    DashboardDetailRows.Add(new InventoryDisplayRowViewModel(row.Label, row.Value));
                }

                _isInventoryLoading = true;
            }

            _ = LoadAsync();
        }

        public string DeviceModel { get => _deviceModel; private set => SetProperty(ref _deviceModel, value); }
        public string SystemSummary { get => _systemSummary; private set => SetProperty(ref _systemSummary, value); }
        public string RuntimeStatus { get => _runtimeStatus; private set => SetProperty(ref _runtimeStatus, value); }
        public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }
        public string CpuPhysicalCores { get => _cpuPhysicalCores; private set => SetProperty(ref _cpuPhysicalCores, value); }
        public string CpuLogicalProcessors { get => _cpuLogicalProcessors; private set => SetProperty(ref _cpuLogicalProcessors, value); }
        public string CpuMaxClock { get => _cpuMaxClock; private set => SetProperty(ref _cpuMaxClock, value); }
        public string GpuNames { get => _gpuNames; private set => SetProperty(ref _gpuNames, value); }
        public string GpuDetails { get => _gpuDetails; private set => SetProperty(ref _gpuDetails, value); }
        public string MemoryCapacity { get => _memoryCapacity; private set => SetProperty(ref _memoryCapacity, value); }
        public string MemoryManufacturers { get => _memoryManufacturers; private set => SetProperty(ref _memoryManufacturers, value); }
        public string MemorySpeeds { get => _memorySpeeds; private set => SetProperty(ref _memorySpeeds, value); }
        public string MemoryModuleCount { get => _memoryModuleCount; private set => SetProperty(ref _memoryModuleCount, value); }
        public string Motherboard { get => _motherboard; private set => SetProperty(ref _motherboard, value); }
        public string MotherboardManufacturer { get => _motherboardManufacturer; private set => SetProperty(ref _motherboardManufacturer, value); }
        public string MotherboardProduct { get => _motherboardProduct; private set => SetProperty(ref _motherboardProduct, value); }
        public string StorageDevices { get => _storageDevices; private set => SetProperty(ref _storageDevices, value); }
        public string OperatingSystem { get => _operatingSystem; private set => SetProperty(ref _operatingSystem, value); }
        public string OperatingSystemName { get => _operatingSystemName; private set => SetProperty(ref _operatingSystemName, value); }
        public string OperatingSystemVersion { get => _operatingSystemVersion; private set => SetProperty(ref _operatingSystemVersion, value); }
        public string OperatingSystemArchitecture { get => _operatingSystemArchitecture; private set => SetProperty(ref _operatingSystemArchitecture, value); }
        public string DetectedAt { get => _detectedAt; private set => SetProperty(ref _detectedAt, value); }

        /// <summary>V2-M4.5C Gate B：顶部第三卡 = 运行时间（来自 OS LastBoot）。</summary>
        public string UptimeDisplay { get => _uptimeDisplay; private set => SetProperty(ref _uptimeDisplay, value); }

        /// <summary>次级行：最近启动时间（yyyy-MM-dd HH:mm）。</summary>
        public string LastBootDisplay { get => _lastBootDisplay; private set => SetProperty(ref _lastBootDisplay, value); }
        public string SensorStatus { get => _sensorStatus; private set => SetProperty(ref _sensorStatus, value); }
        public string SensorCapturedAt { get => _sensorCapturedAt; private set => SetProperty(ref _sensorCapturedAt, value); }
        public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
        public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
        public bool HasSensorData { get => _hasSensorData; private set => SetProperty(ref _hasSensorData, value); }

        public bool IsSensorRefreshing
        {
            get => _isSensorRefreshing;
            private set
            {
                if (!SetProperty(ref _isSensorRefreshing, value))
                {
                    return;
                }

                _refreshSensorsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(RefreshSensorButtonText));
            }
        }

        public string RefreshSensorButtonText =>
            IsSensorRefreshing ? "正在刷新..." : "立即刷新";

        /// <summary>V2-M3.3 §13：紧凑刷新状态（“自动刷新 · 2 秒”/“自动刷新已关闭”）。</summary>
        public string AutoRefreshSummary
        {
            get => _autoRefreshSummary;
            private set => SetProperty(ref _autoRefreshSummary, value);
        }

        /// <summary>V2-M4.5B Gate A：Dashboard "电脑详细信息" 固定顺序行。</summary>
        public ObservableCollection<InventoryDisplayRowViewModel> DashboardDetailRows { get; } = [];

        /// <summary>Dashboard 详情行已装配（inventory 可用）。</summary>
        public bool HasDashboardDetails
        {
            get => _hasDashboardDetails;
            private set => SetProperty(ref _hasDashboardDetails, value);
        }

        /// <summary>V2-M4.5B Gate B：Hardware 详情页左侧静态 Inventory 分区。</summary>
        public ObservableCollection<InventoryDisplaySectionViewModel> InventorySections { get; } = [];

        /// <summary>静态 Inventory 详情可用（ legacy 分组收起）。</summary>
        public bool HasInventoryDetail
        {
            get => _hasInventoryDetail;
            private set => SetProperty(ref _hasInventoryDetail, value);
        }

        /// <summary>Dashboard static inventory startup state; loading keeps the final row shape.</summary>
        public bool IsInventoryLoading
        {
            get => _isInventoryLoading;
            private set => SetProperty(ref _isInventoryLoading, value);
        }

        /// <summary>True only when the shared Rich Inventory task genuinely failed.</summary>
        public bool HasInventoryError
        {
            get => _hasInventoryError;
            private set => SetProperty(ref _hasInventoryError, value);
        }

        public ObservableCollection<HardwareSensorGroupViewModel> SensorGroups { get; } = [];

        /// <summary>V2-M1：统一遥测数据源状态（紧凑区）。</summary>
        public ObservableCollection<TelemetrySourceStatusViewModel> DataSources { get; } = [];


        /// <summary>V2-M4.5C Gate F：右侧实时设备卡（Meter + Current/Low/High）。</summary>
        public ObservableCollection<LiveDeviceCardViewModel> LiveCards { get; } = [];

        /// <summary>V2-M1.1：最近一次快照的 Raw 明细（数据源详情对话框用，§25）。</summary>
        public IReadOnlyList<TelemetryDebugRow> LastDebugRows
        {
            get => _lastDebugRows;
            private set => SetProperty(ref _lastDebugRows, value);
        }

        /// <summary>详情对话框的标题后缀（来源版本等）。</summary>
        public string DebugVersions { get; private set; } = string.Empty;

        public bool HasDataSources
        {
            get => _hasDataSources;
            private set => SetProperty(ref _hasDataSources, value);
        }


        /// <summary>实时设备卡有数据时为 true。</summary>
        public bool HasLiveCards
        {
            get => _hasLiveCards;
            private set
            {
                if (SetProperty(ref _hasLiveCards, value))
                {
                    _resetRangesCommand.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Gate D/G：“重置范围”——清空本次监测期间观察到的 Low/High，
        /// 下一 snapshot 从当前值重新开始。</summary>
        public RelayCommand ResetRangesCommand => _resetRangesCommand;

        public AsyncRelayCommand RefreshSensorsCommand =>
            _refreshSensorsCommand;

        public async Task InitializeSensorsAsync()
        {
            if (_hasInitializedSensors)
            {
                return;
            }

            _hasInitializedSensors = true;
            // Gate D：Hardware 实时监测开始 → 范围从零开始观察。
            _rangeTracker.Reset();
            await RefreshSensorsAsync();
        }

        private async Task LoadAsync()
        {
            // Rich inventory starts immediately and is independent of the legacy
            // WMI detection still needed by Hardware/Diagnosis consumers.
            var inventoryTask = LoadInventoryAsync();
            try
            {
                var hardware = await _hardwareDetectionService.DetectAsync();
                ApplyStaticHardware(hardware);
                RuntimeStatus = HasKnownData(hardware)
                    ? "真实硬件信息已读取"
                    : "检测完成，但系统未返回可用字段";
            }
            catch
            {
                HasError = true;
                RuntimeStatus = "硬件信息读取失败";
                DeviceModel = NotDetectedDisplay;
                SystemSummary = NotDetectedDisplay;
            }
            finally
            {
                // The two tasks are deliberately independent; wait here only so
                // IsLoading describes the complete Hardware page lifecycle.
                await inventoryTask;
                IsLoading = false;
            }
        }

        private async Task RefreshSensorsAsync()
        {
            IsSensorRefreshing = true;
            SensorStatus = "正在刷新实时传感器...";
            try
            {
                var snapshot = await _hardwareSensorService.ReadAsync();
                ApplySensorSnapshot(snapshot);
            }
            catch
            {
                SensorGroups.Clear();
                HasSensorData = false;
                SensorStatus = "当前设备未读取到可用实时传感器数据。";
                SensorCapturedAt = "--";
            }
            finally
            {
                IsSensorRefreshing = false;
            }

            // 统一遥测层：与 V1 传感区并行展示；任何外部源失败都不影响上面已渲染的数据。
            await RefreshTelemetryAsync();
        }

        private async Task RefreshTelemetryAsync()
        {
            if (_telemetryHub is null)
            {
                return;
            }

            try
            {
                var snapshot = await _telemetryHub.ReadAsync();
                ApplyTelemetrySnapshot(snapshot);
            }
            catch
            {
                // Hub 契约上不抛业务异常；此处仅为防御性兜底，
                // 绝不清空 V1 SensorGroups 的既有展示。
                DataSources.Clear();
                HasDataSources = false;
            }
        }

        private void ApplyTelemetrySnapshot(TelemetrySnapshot snapshot)
        {
            DataSources.Clear();
            foreach (var report in snapshot.Sources)
            {
                DataSources.Add(new TelemetrySourceStatusViewModel(report));
            }

            HasDataSources = DataSources.Count > 0;

            // V2-M4.5C.1 Gate B：旧“实时数据 · 核心指标”卡列表已随 UI 移除，
            // 与实时设备卡（LiveCards）完全重复；canonical 数据与 Raw 明细不变。
            AutoRefreshSummary = _liveSource is { IsRunning: true } live
                ? $"自动刷新 · {(live.IntervalMs >= 1000 ? $"{live.IntervalMs / 1000d:0.#} 秒" : $"{live.IntervalMs} ms")}"
                : "自动刷新已关闭";

            // §25 调试明细：Raw → canonical 的对应关系，供验收与调 mapping。
            var canonicalBySource = snapshot.CanonicalReadings
                .GroupBy(reading => (reading.Source, reading.SourceMetricId))
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    EqualityComparer<(TelemetrySourceKind, string)>.Default);
            var rows = new List<TelemetryDebugRow>(snapshot.RawReadings.Count);
            foreach (var raw in snapshot.RawReadings)
            {
                canonicalBySource.TryGetValue(
                    (raw.Source, raw.SourceMetricId),
                    out var canonical);
                rows.Add(new TelemetryDebugRow(
                    TelemetrySourceStatusViewModel.SourceDisplayName(raw.Source),
                    canonical is null ? "—" : $"{canonical.Device.DisplayName}",
                    raw.DeviceInfo.NativeDeviceId,
                    raw.SourceMetricId,
                    raw.Label,
                    FormatMetricValue(raw.Value, raw.Unit),
                    canonical?.MetricKey.Value ?? "—"));
            }

            LastDebugRows = rows;
            DebugVersions = string.Join(" · ", snapshot.Sources
                .Where(report => report.SourceVersion is not null)
                .Select(report => $"{TelemetrySourceStatusViewModel.SourceDisplayName(report.Source)} {report.SourceVersion}"));

            // V2-M4.5C Gate F：右侧实时区 = Meter + Current/Low/High 设备卡。
            RebuildLiveCards(snapshot);
        }

        /// <summary>
        /// V2-M4.5C Gate F：实时设备卡装配收敛到 HardwareLiveViewBuilder；
        /// Low/High 来自 <see cref="_rangeTracker"/>（来源无关，来源 fallback 不断线）。
        /// </summary>
        private void RebuildLiveCards(TelemetrySnapshot snapshot)
        {
            var cards = HardwareLiveViewBuilder.Build(snapshot, _rangeTracker, _inventoryMemoryModules);

            LiveCards.Clear();
            foreach (var card in cards)
            {
                LiveCards.Add(new LiveDeviceCardViewModel(
                    card.Title,
                    card.Meters.Select(meter => new LiveMeterLineViewModel(
                        meter.Label,
                        meter.Current,
                        meter.Low,
                        meter.High,
                        meter.ScaleMin,
                        meter.ScaleMax,
                        HardwareLiveViewBuilder.FormatValue(meter.Current, meter.Unit),
                        HardwareLiveViewBuilder.FormatValue(meter.Low ?? meter.Current, meter.Unit),
                        HardwareLiveViewBuilder.FormatValue(meter.High ?? meter.Current, meter.Unit)))
                        .ToArray(),
                    card.Numerics.Select(numeric => new LiveNumericLineViewModel(
                        numeric.Label,
                        numeric.Current,
                        numeric.Low,
                        numeric.High))
                        .ToArray(),
                    card.SubLines.Select(subLine => subLine.Text)
                        .ToArray()));
            }

            HasLiveCards = LiveCards.Count > 0;
        }

        private static string? MetricLabel(TelemetryMetricKey metric) =>
            metric.Value switch
            {
                "cpu.package.temperature" => "温度",
                "cpu.package.power" => "Package Power",
                "cpu.total.utilization" => "使用率",
                "cpu.clock" => "时钟频率",
                "cpu.throttling" => "降频占比",
                "gpu.core.temperature" => "温度",
                "gpu.hotspot.temperature" => "热点温度",
                "gpu.memory.temperature" => "显存温度",
                "gpu.board.power" => "功耗",
                "gpu.core.utilization" => "使用率",
                "gpu.core.clock" => "核心频率",
                "gpu.memory.clock" => "显存频率",
                "gpu.memory.used" => "已用显存",
                "memory.used" => "已用内存",
                "memory.utilization" => "使用率",
                "memory.clock" => "内存频率",
                "storage.temperature" => "温度",
                _ => null
            };

        private static string FormatMetricValue(double value, TelemetryUnit unit) =>
            unit switch
            {
                TelemetryUnit.Celsius => $"{value:0.#} °C",
                TelemetryUnit.Watt => $"{value:0.#} W",
                TelemetryUnit.Megahertz => $"{value:0} MHz",
                TelemetryUnit.Percent => $"{value:0.#} %",
                TelemetryUnit.Volt => $"{value:0.###} V",
                TelemetryUnit.Byte => $"{value / 1073741824d:0.##} GB",
                _ => $"{value:0.###}"
            };

        /// <summary>
        /// V2-M4.5B Gate A/B：静态 Inventory → Dashboard 行 + 详情页分区。
        /// 单独 try/catch（Gate M 语义）：inventory 失败只影响新区块，
        /// 绝不清空既有静态硬件/实时遥测展示。
        /// </summary>
        private async Task LoadInventoryAsync()
        {
            if (_hardwareInventoryService is null)
            {
                HasInventoryError = true;
                IsInventoryLoading = false;
                return;
            }

            try
            {
                var snapshot = await _hardwareInventoryService.CollectAsync();
                ApplyInventory(snapshot);
            }
            catch
            {
                // Legacy rows are permitted only for this genuine Rich Inventory
                // failure path; normal startup remains on the final Rich shape.
                HasInventoryError = true;
                HasDashboardDetails = false;
                HasInventoryDetail = false;
                DashboardDetailRows.Clear();
                InventorySections.Clear();
            }
            finally
            {
                IsInventoryLoading = false;
            }
        }

        private void ApplyInventory(HardwareInventorySnapshot snapshot)
        {
            _inventoryMemoryModules = snapshot.MemoryModules;

            // Gate B：顶部第三卡改为运行时间（OS LastBoot / uptime）。
            if (snapshot.Os?.LastBootUtc is { } lastBoot)
            {
                // M5.2C：记录最近启动时间；显示值由逐秒时钟按"当前时刻 -
                // LastBoot"计算，不再依赖采集时刻，也不触发任何重新采集。
                _uptimeLastBootUtc = lastBoot;
                RefreshUptimeDisplay();
                LastBootDisplay = "最近启动 "
                    + lastBoot.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                UptimeDisplay = "--";
                LastBootDisplay = "--";
            }

            // V2-M4.5C.1 Gate A：先在局部完整构建新的 presentation 视图模型，
            // 再对 UI 集合做一次 Clear+Add 的原子切换 —— 两个 await/事件之间
            // 绝不产生可被用户看到的中间态（legacy 行与新行并存的窗口）。
            var dashboardRows = DashboardInventoryPresenter.BuildRows(snapshot)
                .Select(row => new InventoryDisplayRowViewModel(row.Label, row.Value))
                .ToArray();
            var sections = HardwareInventoryDetailPresenter.BuildSections(snapshot)
                .Select(section => new InventoryDisplaySectionViewModel(
                    section.Title,
                    section.Cards
                        .Select(card => new InventoryDisplayCardViewModel(
                            card.Title,
                            card.Rows
                                .Select(row => new InventoryDisplayRowViewModel(row.Label, row.Value))
                                .ToArray()))
                        .ToArray()))
                .ToArray();

            DashboardDetailRows.Clear();
            foreach (var row in dashboardRows)
            {
                DashboardDetailRows.Add(row);
            }

            HasDashboardDetails = dashboardRows.Length > 0;
            HasInventoryError = false;

            InventorySections.Clear();
            foreach (var section in sections)
            {
                InventorySections.Add(section);
            }

            HasInventoryDetail = sections.Length > 0;
        }

        /// <summary>
        /// M5.2C：仪表盘运行时间逐秒时钟。仅做字符串计算（当前时刻 -
        /// LastBoot），不触碰 telemetry、不触发 inventory refresh；
        /// 由 Dashboard 页面 Loaded/Unloaded 驱动启停。
        /// </summary>
        public void StartUptimeClock()
        {
            if (_uptimeTimer is not null)
            {
                return;
            }

            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += (_, _) => RefreshUptimeDisplay();
            _uptimeTimer = timer;
            RefreshUptimeDisplay();
            timer.Start();
        }

        public void StopUptimeClock()
        {
            _uptimeTimer?.Stop();
            _uptimeTimer = null;
        }

        private void RefreshUptimeDisplay()
        {
            if (_uptimeLastBootUtc is not { } lastBoot)
            {
                return;
            }

            UptimeDisplay = HardwareInventoryDetailPresenter.FormatUptime(
                DateTimeOffset.UtcNow - lastBoot);
        }

        private void ApplyStaticHardware(HardwareInfo hardware)
        {
            CpuName = Known(hardware.CpuName);
            CpuPhysicalCores = FormatCount(hardware.CpuPhysicalCoreCount, " 核");
            CpuLogicalProcessors = FormatCount(
                hardware.CpuLogicalProcessorCount,
                " 线程");
            CpuMaxClock = hardware.CpuMaxClockSpeedMHz.HasValue
                ? $"{hardware.CpuMaxClockSpeedMHz.Value} MHz"
                : NotDetectedDisplay;

            GpuNames = JoinKnown(hardware.GpuNames);
            GpuDetails = JoinKnown(hardware.GpuNames, Environment.NewLine);

            MemoryCapacity = hardware.TotalMemoryBytes is > 0
                ? FormatBytes(hardware.TotalMemoryBytes.Value)
                : NotDetectedDisplay;
            MemoryManufacturers = JoinKnown(hardware.MemoryManufacturers);
            MemorySpeeds = hardware.MemorySpeedsMHz.Count > 0
                ? string.Join(
                    " / ",
                    hardware.MemorySpeedsMHz
                        .Distinct()
                        .Select(speed => $"{speed} MHz"))
                : NotDetectedDisplay;
            MemoryModuleCount = hardware.MemoryModuleCount.HasValue
                ? $"{hardware.MemoryModuleCount.Value} 条"
                : NotDetectedDisplay;

            MotherboardManufacturer = Known(hardware.MotherboardManufacturer);
            MotherboardProduct = Known(hardware.MotherboardProduct);
            Motherboard = JoinParts(
                hardware.MotherboardManufacturer,
                hardware.MotherboardProduct);

            StorageDevices = FormatStorageDevices(hardware.StorageDevices);

            OperatingSystemName = Known(hardware.OperatingSystemName);
            OperatingSystemVersion = Known(hardware.OperatingSystemVersion);
            OperatingSystemArchitecture = Known(
                hardware.OperatingSystemArchitecture);
            OperatingSystem = JoinParts(
                hardware.OperatingSystemName,
                hardware.OperatingSystemVersion,
                hardware.OperatingSystemArchitecture);

            DeviceModel = Motherboard;
            SystemSummary = OperatingSystem;
            DetectedAt = hardware.DetectedAt == default
                ? "检测时间不可用"
                : hardware.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void ApplySensorSnapshot(HardwareSensorSnapshot snapshot)
        {
            SensorGroups.Clear();
            AddSensorGroup(
                "CPU 状态",
                ("使用率", FormatPercent(snapshot.CpuLoadPercent)),
                ("温度", FormatTemperature(snapshot.CpuTemperatureCelsius)),
                ("时钟频率", FormatFrequency(snapshot.CpuClockMHz)),
                ("Package Power", FormatPower(snapshot.CpuPackagePowerWatts)),
                ("电压", FormatVoltage(snapshot.CpuVoltageVolts)));
            AddSensorGroup(
                string.IsNullOrWhiteSpace(snapshot.GpuName)
                    ? "GPU 状态"
                    : $"GPU 状态 · {snapshot.GpuName}",
                ("使用率", FormatPercent(snapshot.GpuLoadPercent)),
                ("温度", FormatTemperature(snapshot.GpuTemperatureCelsius)),
                ("核心频率", FormatFrequency(snapshot.GpuCoreClockMHz)),
                ("显存频率", FormatFrequency(snapshot.GpuMemoryClockMHz)),
                ("功耗", FormatPower(snapshot.GpuPowerWatts)));
            AddSensorGroup(
                "内存状态",
                ("使用率", FormatPercent(snapshot.MemoryLoadPercent)));

            if (snapshot.StorageTemperatures.Count > 0)
            {
                SensorGroups.Add(new HardwareSensorGroupViewModel(
                    "磁盘温度",
                    snapshot.StorageTemperatures
                        .Select(reading => new HardwareSensorItemViewModel(
                            reading.StorageName,
                            $"{reading.TemperatureCelsius:0.#} °C"))
                        .ToArray()));
            }

            HasSensorData = SensorGroups.Count > 0;
            SensorStatus = HasSensorData
                ? $"已读取 {SensorGroups.Sum(group => group.Items.Count)} 项实时传感器数据"
                : "当前设备未读取到可用实时传感器数据。";
            SensorCapturedAt = snapshot.CapturedAt == default
                ? "--"
                : snapshot.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void AddSensorGroup(
            string title,
            params (string Label, string? Value)[] values)
        {
            var items = values
                .Where(item => item.Value is not null)
                .Select(item => new HardwareSensorItemViewModel(
                    item.Label,
                    item.Value!))
                .ToArray();
            if (items.Length > 0)
            {
                SensorGroups.Add(new HardwareSensorGroupViewModel(title, items));
            }
        }

        private static string? FormatPercent(double? value) =>
            value.HasValue ? $"{value.Value:0.#} %" : null;

        private static string? FormatTemperature(double? value) =>
            value.HasValue ? $"{value.Value:0.#} °C" : null;

        private static string? FormatFrequency(double? value) =>
            value.HasValue ? $"{value.Value:0} MHz" : null;

        private static string? FormatPower(double? value) =>
            value.HasValue ? $"{value.Value:0.#} W" : null;

        private static string? FormatVoltage(double? value) =>
            value.HasValue ? $"{value.Value:0.###} V" : null;

        private static string FormatCount(uint? value, string suffix) =>
            value.HasValue ? $"{value.Value}{suffix}" : Display(HardwareInfo.UnknownValue);

        private static string FormatStorageDevices(
            IReadOnlyList<StorageDeviceInfo> devices)
        {
            if (devices.Count == 0)
            {
                return Display(HardwareInfo.UnknownValue);
            }

            return string.Join(
                Environment.NewLine,
                devices.Select(device =>
                {
                    var capacity = device.CapacityBytes.HasValue
                        ? FormatBytes(device.CapacityBytes.Value)
                        : NotDetectedDisplay;
                    return $"{Known(device.Model)}  ·  {capacity}  ·  {Known(device.MediaType)}";
                }));
        }

        private static string FormatBytes(ulong bytes) =>
            $"{bytes / 1024d / 1024d / 1024d:0.##} GB";

        // 展示层专用：业务层哨兵值 “Unknown” 在界面上统一显示为中文。
        private const string NotDetectedDisplay = "未检测到";

        private static string Display(string value) =>
            string.Equals(value, HardwareInfo.UnknownValue, StringComparison.OrdinalIgnoreCase)
                ? NotDetectedDisplay
                : value;

        private static string Known(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? Display(HardwareInfo.UnknownValue)
                : Display(value);

        private static string JoinKnown(
            IEnumerable<string>? values,
            string separator = " / ")
        {
            var known = values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return known?.Length > 0
                ? string.Join(separator, known)
                : Display(HardwareInfo.UnknownValue);
        }

        private static string JoinParts(params string?[] values)
        {
            var parts = values
                .Where(value =>
                    !string.IsNullOrWhiteSpace(value)
                    && !string.Equals(
                        value,
                        HardwareInfo.UnknownValue,
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return parts.Length > 0
                ? string.Join(" · ", parts)
                : NotDetectedDisplay;
        }

        private static bool HasKnownData(HardwareInfo hardware) =>
            !string.Equals(
                hardware.CpuName,
                HardwareInfo.UnknownValue,
                StringComparison.OrdinalIgnoreCase)
            || hardware.TotalMemoryBytes is > 0
            || hardware.GpuNames.Any(value =>
                !string.Equals(
                    value,
                    HardwareInfo.UnknownValue,
                    StringComparison.OrdinalIgnoreCase));
    }
}