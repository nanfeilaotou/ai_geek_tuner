using AIGeekTuner.Models;
using LibreHardwareMonitor.Hardware;

namespace AIGeekTuner.Services.Hardware
{
    public sealed class LibreHardwareMonitorSensorService
        : IHardwareSensorService
    {
        public async Task<HardwareSensorSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await Task.Run(
                    () => ReadCore(cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Sensor access is optional. Unsupported hardware, missing
                // drivers, or insufficient privileges must degrade to no data.
                return new HardwareSensorSnapshot
                {
                    CapturedAt = DateTimeOffset.UtcNow
                };
            }
        }

        private static HardwareSensorSnapshot ReadCore(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true
            };

            try
            {
                computer.Open();
                var readings = new List<SensorReading>();
                foreach (var hardware in computer.Hardware)
                {
                    CollectReadings(
                        hardware,
                        readings,
                        cancellationToken);
                }

                return CreateSnapshot(readings);
            }
            finally
            {
                try
                {
                    computer.Close();
                }
                catch
                {
                    // Closing a partially opened provider must not mask data
                    // already read or surface as an application failure.
                }
            }
        }

        private static void CollectReadings(
            IHardware hardware,
            ICollection<SensorReading> readings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hardware.Update();

            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue
                    || !float.IsFinite(sensor.Value.Value))
                {
                    continue;
                }

                readings.Add(new SensorReading(
                    hardware.HardwareType,
                    hardware.Name,
                    sensor.SensorType,
                    sensor.Name,
                    sensor.Value.Value));
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                CollectReadings(
                    subHardware,
                    readings,
                    cancellationToken);
            }
        }

        private static HardwareSensorSnapshot CreateSnapshot(
            IReadOnlyList<SensorReading> readings)
        {
            var cpu = readings
                .Where(reading => reading.HardwareType == HardwareType.Cpu)
                .ToArray();
            var memory = readings
                .Where(reading => reading.HardwareType == HardwareType.Memory)
                .ToArray();
            var gpu = SelectGpuReadings(readings);

            return new HardwareSensorSnapshot
            {
                CpuTemperatureCelsius = SelectPreferred(
                    cpu,
                    SensorType.Temperature,
                    ["Package", "Core Max", "Tctl", "Tdie"],
                    requirePositiveValue: true),
                CpuLoadPercent = SelectPreferred(
                    cpu,
                    SensorType.Load,
                    ["CPU Total", "Total"]),
                CpuClockMHz = SelectPreferred(
                    cpu,
                    SensorType.Clock,
                    ["Core Average", "Core #1"],
                    requirePositiveValue: true),
                CpuPackagePowerWatts = SelectPreferred(
                    cpu,
                    SensorType.Power,
                    ["Package", "CPU Package"],
                    requirePositiveValue: true),
                CpuVoltageVolts = SelectPreferred(
                    cpu,
                    SensorType.Voltage,
                    ["Core Average", "Vcore", "CPU Core"],
                    requirePositiveValue: true),
                GpuName = gpu.Count > 0 ? gpu[0].HardwareName : null,
                GpuTemperatureCelsius = SelectPreferred(
                    gpu,
                    SensorType.Temperature,
                    ["GPU Core", "Core"],
                    requirePositiveValue: true),
                GpuLoadPercent = SelectPreferred(
                    gpu,
                    SensorType.Load,
                    ["GPU Core", "Core"]),
                GpuCoreClockMHz = SelectPreferred(
                    gpu,
                    SensorType.Clock,
                    ["GPU Core", "Core"],
                    requirePositiveValue: true),
                GpuMemoryClockMHz = SelectPreferred(
                    gpu,
                    SensorType.Clock,
                    ["GPU Memory", "Memory"],
                    requirePositiveValue: true),
                GpuPowerWatts = SelectPreferred(
                    gpu,
                    SensorType.Power,
                    ["GPU Package", "Package", "GPU Power"],
                    requirePositiveValue: true),
                MemoryLoadPercent = SelectPreferred(
                    memory,
                    SensorType.Load,
                    ["Memory", "Load"]),
                StorageTemperatures = ReadStorageTemperatures(readings),
                CapturedAt = DateTimeOffset.UtcNow
            };
        }

        private static IReadOnlyList<SensorReading> SelectGpuReadings(
            IReadOnlyList<SensorReading> readings)
        {
            var candidates = readings
                .Where(reading => reading.HardwareType is
                    HardwareType.GpuNvidia
                    or HardwareType.GpuAmd
                    or HardwareType.GpuIntel)
                .GroupBy(reading => new
                {
                    reading.HardwareType,
                    reading.HardwareName
                })
                .OrderBy(group => GpuPriority(group.Key.HardwareType))
                .ThenByDescending(group => group.Count())
                .FirstOrDefault();

            return candidates?.ToArray() ?? Array.Empty<SensorReading>();
        }

        private static int GpuPriority(HardwareType hardwareType) =>
            hardwareType switch
            {
                HardwareType.GpuNvidia => 0,
                HardwareType.GpuAmd => 1,
                HardwareType.GpuIntel => 2,
                _ => 3
            };

        private static double? SelectPreferred(
            IReadOnlyList<SensorReading> readings,
            SensorType sensorType,
            IReadOnlyList<string> preferredNames,
            bool requirePositiveValue = false)
        {
            var candidates = readings
                .Where(reading =>
                    reading.SensorType == sensorType
                    && (!requirePositiveValue || reading.Value > 0))
                .ToArray();
            if (candidates.Length == 0)
            {
                return null;
            }

            foreach (var preferredName in preferredNames)
            {
                var preferred = candidates.FirstOrDefault(reading =>
                    reading.SensorName.Contains(
                        preferredName,
                        StringComparison.OrdinalIgnoreCase));
                if (preferred is not null)
                {
                    return preferred.Value;
                }
            }

            return candidates.Max(reading => reading.Value);
        }

        private static IReadOnlyList<StorageTemperatureReading>
            ReadStorageTemperatures(IReadOnlyList<SensorReading> readings)
        {
            return readings
                .Where(reading =>
                    reading.HardwareType == HardwareType.Storage
                    && reading.SensorType == SensorType.Temperature
                    && reading.Value > 0)
                .GroupBy(reading => reading.HardwareName)
                .Select(group => new StorageTemperatureReading
                {
                    StorageName = group.Key,
                    TemperatureCelsius = group.Max(reading => reading.Value)
                })
                .OrderBy(reading => reading.StorageName)
                .ToArray();
        }

        private sealed record SensorReading(
            HardwareType HardwareType,
            string HardwareName,
            SensorType SensorType,
            string SensorName,
            double Value);
    }
}
