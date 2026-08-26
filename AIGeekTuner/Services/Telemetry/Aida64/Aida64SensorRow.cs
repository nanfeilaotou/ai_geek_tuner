namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>AIDA64 WMI 行的原始字符串形态：解析决策全部留在 Provider 层。</summary>
    public sealed record Aida64SensorRow(string? Id, string? Label, string? Value, string? Type);
}
