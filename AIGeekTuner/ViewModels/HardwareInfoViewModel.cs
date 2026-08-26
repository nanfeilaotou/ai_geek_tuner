using System.Collections.ObjectModel;
using AIGeekTuner.Commands;
using AIGeekTuner.Models;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Hardware;
using AIGeekTuner.Services.Telemetry;

namespace AIGeekTuner.ViewModels
{
    public sealed class HardwareInfoViewModel : ViewModelBase
    {
        private readonly IHardwareDetectionService _hardwareDetectionService;
        private readonly IHardwareSensorService _hardwareSensorService;
        private readonly ITelemetryHub? _telemetryHub;
        private readonly AsyncRelayCommand _refreshSensorsCommand;

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
        private string _sensorStatus = "等待读取实时传感器";
        private string _sensorCapturedAt = "--";
        private bool _isLoading = true;
        private bool _hasError;
        private bool _isSensorRefreshing;
        private bool _hasSensorData;
        private bool _hasInitializedSensors;
        private bool _hasDataSources;
        private bool _hasCoreMetrics;
        private IReadOnlyList<TelemetryDebugRow> _lastDebugRows = [];

        public HardwareInfoViewModel(
            IHardwareDetectionService hardwareDetectionService,
            IHardwareSensorService hardwareSensorService,
            ITelemetryHub? telemetryHub = null)
        {
            _hardwareDetectionService = hardwareDetectionService
                ?? throw new ArgumentNullException(nameof(hardwareDetectionService));
            _hardwareSensorService = hardwareSensorService
                ?? throw new ArgumentNullException(nameof(hardwareSensorService));
            _telemetryHub = telemetryHub;
            _refreshSensorsCommand = new AsyncRelayCommand(
                RefreshSensorsAsync,
                () => !IsSensorRefreshing);

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
            IsSensorRefreshing ? "正在刷新..." : "刷新实时状态";

        public ObservableCollection<HardwareSensorGroupViewModel> SensorGroups { get; } = [];

        /// <summary>V2-M1：统一遥测数据源状态（紧凑区）。</summary>
        public ObservableCollection<TelemetrySourceStatusViewModel> DataSources { get; } = [];

        /// <summary>V2-M1：canonical 核心指标分组展示。</summary>
        public ObservableCollection<HardwareSensorGroupViewModel> CoreMetricGroups { get; } = [];

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

        public bool HasCoreMetrics
        {
            get => _hasCoreMetrics;
            private set => SetProperty(ref _hasCoreMetrics, value);
        }

        public AsyncRelayCommand RefreshSensorsCommand =>
            _refreshSensorsCommand;

        public async Task InitializeSensorsAsync()
        {
            if (_hasInitializedSensors)
            {
                return;
            }

            _hasInitializedSensors = true;
            await RefreshSensorsAsync();
        }

        private async Task LoadAsync()
        {
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
                CoreMetricGroups.Clear();
                HasDataSources = false;
                HasCoreMetrics = false;
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

            CoreMetricGroups.Clear();
            foreach (var group in snapshot.CanonicalReadings
                .OrderBy(reading => DeviceOrder(reading.Device.Kind))
                .ThenBy(reading => reading.Device.DeviceKey, StringComparer.Ordinal)
                .ThenBy(reading => MetricOrder(reading.MetricKey))
                .GroupBy(reading => (reading.Device.Kind, reading.Device.DeviceKey)))
            {
                var items = group
                    .Select(reading => (Reading: reading, Label: MetricLabel(reading.MetricKey)))
                    .Where(entry => entry.Label is not null)
                    .Select(entry => new HardwareSensorItemViewModel(
                        entry.Label!,
                        FormatMetricValue(entry.Reading.Value, entry.Reading.Unit),
                        TelemetrySourceStatusViewModel.SourceDisplayName(entry.Reading.Source)))
                    .ToArray();
                if (items.Length > 0)
                {
                    CoreMetricGroups.Add(new HardwareSensorGroupViewModel(
                        DeviceGroupTitle(group.Key.Kind, group.First().Device.DisplayName),
                        items));
                }
            }

            HasCoreMetrics = CoreMetricGroups.Count > 0;

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
        }

        private static int DeviceOrder(TelemetryDeviceKind kind) =>
            kind switch
            {
                TelemetryDeviceKind.Cpu => 0,
                TelemetryDeviceKind.Gpu => 1,
                TelemetryDeviceKind.Memory => 2,
                TelemetryDeviceKind.Storage => 3,
                _ => 4
            };

        private static string DeviceGroupTitle(TelemetryDeviceKind kind, string displayName) =>
            kind switch
            {
                TelemetryDeviceKind.Cpu => $"CPU · {displayName}",
                TelemetryDeviceKind.Gpu => $"GPU · {displayName}",
                TelemetryDeviceKind.Memory => "内存",
                TelemetryDeviceKind.Storage => $"磁盘 · {displayName}",
                _ => displayName
            };

        private static int MetricOrder(TelemetryMetricKey metric) =>
            metric.Value switch
            {
                "cpu.package.temperature" => 0,
                "cpu.total.utilization" => 1,
                "cpu.clock" => 2,
                "cpu.package.power" => 3,
                "cpu.throttling" => 4,
                "gpu.core.temperature" => 0,
                "gpu.hotspot.temperature" => 1,
                "gpu.memory.temperature" => 2,
                "gpu.core.utilization" => 3,
                "gpu.core.clock" => 4,
                "gpu.board.power" => 5,
                "gpu.memory.used" => 6,
                "memory.used" => 0,
                "memory.utilization" => 1,
                "memory.clock" => 2,
                "storage.temperature" => 0,
                _ => 99
            };

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
