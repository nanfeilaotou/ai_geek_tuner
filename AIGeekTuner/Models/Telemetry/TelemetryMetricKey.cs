namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// 规范化指标键的封闭集合：本轮只收录真正有诊断意义的核心指标。
    /// 值字符串同时是稳定的序列化标识，不允许运行期动态构造新指标。
    /// </summary>
    public sealed record TelemetryMetricKey(string Value)
    {
        public static readonly TelemetryMetricKey CpuPackageTemperature = new("cpu.package.temperature");
        public static readonly TelemetryMetricKey CpuTotalUtilization = new("cpu.total.utilization");
        public static readonly TelemetryMetricKey CpuClock = new("cpu.clock");
        public static readonly TelemetryMetricKey CpuPackagePower = new("cpu.package.power");
        public static readonly TelemetryMetricKey CpuThrottling = new("cpu.throttling");

        public static readonly TelemetryMetricKey GpuCoreTemperature = new("gpu.core.temperature");
        public static readonly TelemetryMetricKey GpuHotspotTemperature = new("gpu.hotspot.temperature");
        public static readonly TelemetryMetricKey GpuMemoryTemperature = new("gpu.memory.temperature");
        public static readonly TelemetryMetricKey GpuBoardPower = new("gpu.board.power");
        public static readonly TelemetryMetricKey GpuCoreUtilization = new("gpu.core.utilization");
        public static readonly TelemetryMetricKey GpuCoreClock = new("gpu.core.clock");
        public static readonly TelemetryMetricKey GpuMemoryUsed = new("gpu.memory.used");

        public static readonly TelemetryMetricKey MemoryUsed = new("memory.used");
        public static readonly TelemetryMetricKey MemoryUtilization = new("memory.utilization");
        public static readonly TelemetryMetricKey MemoryClock = new("memory.clock");

        public static readonly TelemetryMetricKey StorageTemperature = new("storage.temperature");

        public override string ToString() => Value;
    }
}
