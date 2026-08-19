using System.IO;
using System.Globalization;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Hardware
{
    public static class HardwareDetectionTest
    {
        public static async Task<HardwareInfo> RunAsync(
            IHardwareDetectionService detectionService,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(detectionService);
            ArgumentNullException.ThrowIfNull(output);

            var hardware = await detectionService.DetectAsync(cancellationToken);

            await output.WriteLineAsync($"CPU: {hardware.CpuName}");
            await output.WriteLineAsync($"GPU: {FormatStrings(hardware.GpuNames)}");
            await output.WriteLineAsync(
                $"Physical memory: {FormatMemorySize(hardware.TotalMemoryBytes)}");
            await output.WriteLineAsync(
                $"Memory manufacturer: {FormatStrings(hardware.MemoryManufacturers)}");
            await output.WriteLineAsync(
                $"Memory speed: {FormatSpeeds(hardware.MemorySpeedsMHz)}");
            await output.WriteLineAsync(
                $"Motherboard: {hardware.MotherboardManufacturer} {hardware.MotherboardProduct}");
            await output.WriteLineAsync(
                $"Operating system: {hardware.OperatingSystemName}");
            await output.WriteLineAsync(
                $"OS version: {hardware.OperatingSystemVersion}");
            await output.WriteLineAsync(
                $"OS architecture: {hardware.OperatingSystemArchitecture}");

            return hardware;
        }

        private static string FormatMemorySize(ulong? bytes)
        {
            if (!bytes.HasValue)
            {
                return HardwareInfo.UnknownValue;
            }

            var gibibytes = bytes.Value / 1024d / 1024d / 1024d;
            return $"{gibibytes.ToString("0.##", CultureInfo.InvariantCulture)} GiB ({bytes.Value} bytes)";
        }

        private static string FormatStrings(IReadOnlyList<string> values) =>
            values.Count == 0
                ? HardwareInfo.UnknownValue
                : string.Join(", ", values);

        private static string FormatSpeeds(IReadOnlyList<uint> speeds) =>
            speeds.Count == 0
                ? HardwareInfo.UnknownValue
                : string.Join(", ", speeds.Select(speed => $"{speed} MHz"));
    }
}
