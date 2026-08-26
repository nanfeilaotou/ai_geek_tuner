namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>遥测数据来源；数值即默认选择优先级（小者优先）。</summary>
    public enum TelemetrySourceKind
    {
        HwInfo = 0,

        Aida64 = 1,

        LibreHardwareMonitor = 2
    }
}
