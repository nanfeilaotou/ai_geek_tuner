using AIGeekTuner.Services.Telemetry.LibreHardwareMonitor;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry;

public sealed class LibreHardwareMonitorSafetyTests
{
    [Fact]
    public void CoreComputer_DisablesGpuEnumeration()
    {
        var computer = LibreHardwareMonitorComputerFactory.CreateCoreComputer();

        Assert.True(computer.IsCpuEnabled);
        Assert.False(computer.IsGpuEnabled);
        Assert.True(computer.IsMemoryEnabled);
        Assert.True(computer.IsStorageEnabled);
    }

    [Fact]
    public void GpuComputer_DisablesCpuAndNonGpuEnumeration()
    {
        var computer = LibreHardwareMonitorComputerFactory.CreateGpuComputer();

        Assert.False(computer.IsCpuEnabled);
        Assert.True(computer.IsGpuEnabled);
        Assert.False(computer.IsMemoryEnabled);
        Assert.False(computer.IsStorageEnabled);
    }

    [Fact]
    public void ProductionScopes_CannotEnableCpuAndGpuTogether()
    {
        var core = LibreHardwareMonitorComputerFactory.CreateCoreComputer();
        var gpu = LibreHardwareMonitorComputerFactory.CreateGpuComputer();

        Assert.False(core.IsCpuEnabled && core.IsGpuEnabled);
        Assert.False(gpu.IsCpuEnabled && gpu.IsGpuEnabled);
    }
}
