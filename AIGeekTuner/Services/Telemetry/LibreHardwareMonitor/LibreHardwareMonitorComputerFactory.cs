using LibreHardwareMonitor.Hardware;

namespace AIGeekTuner.Services.Telemetry.LibreHardwareMonitor;

/// <summary>
/// Creates the two deliberately disjoint LHM hardware scopes used by the app.
/// Keeping CPU and GPU in separate Computer instances prevents LHM 0.9.6 from
/// constructing IntelGpuGroup while GPU enumeration is enabled.
/// </summary>
public static class LibreHardwareMonitorComputerFactory
{
    public static Computer CreateCoreComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = false,
        IsMemoryEnabled = true,
        IsStorageEnabled = true
    };

    public static Computer CreateGpuComputer() => new()
    {
        IsCpuEnabled = false,
        IsGpuEnabled = true,
        IsMemoryEnabled = false,
        IsStorageEnabled = false
    };
}
