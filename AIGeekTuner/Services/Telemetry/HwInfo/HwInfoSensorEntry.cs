namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>父传感器条目：与官方“传感器元素 + 读数元素”两层结构同构的内部抽象。</summary>
    public sealed record HwInfoSensorEntry(uint SensorIndex, string SensorName, string? SensorPath = null);

    /// <summary>
    /// 读数条目。<see cref="SensorIndex"/> 只在父传感器内唯一，
    /// 稳定键必须组合 sensorIndex + readingId（绝不用 readingId 当全局键）。
    /// <see cref="ReadingType"/> 是官方类型码：0=None 1=Temp 2=Volt 3=Fan
    /// 4=Current 5=Power 6=Clock 7=Usage 8+=Other。
    /// </summary>
    public sealed record HwInfoReadingEntry(
        uint SensorIndex,
        uint ReadingId,
        string Label,
        string? Unit,
        double Value,
        uint ReadingType = 0);
}
