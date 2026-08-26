using System.Collections.ObjectModel;
using AIGeekTuner.Commands;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Hardware;

namespace AIGeekTuner.ViewModels
{
    public sealed class HardwareInfoViewModel : ViewModelBase
    {
        private readonly IHardwareDetectionService _hardwareDetectionService;
        private readonly IHardwareSensorService _hardwareSensorService;
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

        public HardwareInfoViewModel(
            IHardwareDetectionService hardwareDetectionService,
            IHardwareSensorService hardwareSensorService)
        {
            _hardwareDetectionService = hardwareDetectionService
                ?? throw new ArgumentNullException(nameof(hardwareDetectionService));
            _hardwareSensorService = hardwareSensorService
                ?? throw new ArgumentNullException(nameof(hardwareSensorService));
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
