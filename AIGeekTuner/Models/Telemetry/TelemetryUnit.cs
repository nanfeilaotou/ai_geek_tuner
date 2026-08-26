namespace AIGeekTuner.Models.Telemetry
{
    public enum TelemetryUnit
    {
        None = 0,

        // ---- canonical 单位（归一化目标） ----
        Celsius = 1,

        Watt = 2,

        Megahertz = 3,

        Percent = 4,

        Volt = 5,

        Byte = 6,

        // ---- 原生量级（仅用于 Raw 层保真，canonical 前必须转换） ----
        Megabyte = 7,

        Gigabyte = 8
    }
}
