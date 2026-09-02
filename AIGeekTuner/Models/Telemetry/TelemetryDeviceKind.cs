namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>设备类别；同一类别可以有任意多个设备实例。</summary>
    public enum TelemetryDeviceKind
    {
        System = 0,

        Cpu = 1,

        Gpu = 2,

        Memory = 3,

        Storage = 4,

        /// <summary>V2-M4.5B：单根内存模块（DIMM）。DeviceKey 形如 memory-module:N。</summary>
        MemoryModule = 5
    }
}
