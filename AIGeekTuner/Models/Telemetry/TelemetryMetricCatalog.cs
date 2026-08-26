namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// Canonical Metric Catalog —— 本轮唯一事实源。
    /// V2-M1.1 审计结论：有效 canonical 指标共 <see cref="All"/>.Count = <b>16</b>
    /// （M1 报告曾误写为 18，以本类为准）。每个键都必须至少被一个 Provider 真实映射；
    /// 不允许存在 phantom / dead key。新增指标必须同步：
    /// 本目录 + 各来源 mapper + UI 标签 + 测试。
    /// </summary>
    public static class TelemetryMetricCatalog
    {
        public static IReadOnlyList<TelemetryMetricKey> Cpu { get; } =
        [
            TelemetryMetricKey.CpuPackageTemperature,
            TelemetryMetricKey.CpuTotalUtilization,
            TelemetryMetricKey.CpuClock,
            TelemetryMetricKey.CpuPackagePower,
            TelemetryMetricKey.CpuThrottling,
        ];

        public static IReadOnlyList<TelemetryMetricKey> Gpu { get; } =
        [
            TelemetryMetricKey.GpuCoreTemperature,
            TelemetryMetricKey.GpuHotspotTemperature,
            TelemetryMetricKey.GpuMemoryTemperature,
            TelemetryMetricKey.GpuBoardPower,
            TelemetryMetricKey.GpuCoreUtilization,
            TelemetryMetricKey.GpuCoreClock,
            TelemetryMetricKey.GpuMemoryUsed,
        ];

        public static IReadOnlyList<TelemetryMetricKey> Memory { get; } =
        [
            TelemetryMetricKey.MemoryUsed,
            TelemetryMetricKey.MemoryUtilization,
            TelemetryMetricKey.MemoryClock,
        ];

        public static IReadOnlyList<TelemetryMetricKey> Storage { get; } =
        [
            TelemetryMetricKey.StorageTemperature,
        ];

        public static IReadOnlyList<TelemetryMetricKey> All { get; } =
            Cpu.Concat(Gpu).Concat(Memory).Concat(Storage).ToArray();
    }
}
