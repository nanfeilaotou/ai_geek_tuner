namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// 设备身份。<paramref name="DeviceKey"/> 在当前运行/硬件条件下必须稳定，
    /// 且在同一 <see cref="Kind"/> 内唯一——绝不允许用 UI 显示名称当唯一 ID。
    /// </summary>
    public sealed record TelemetryDeviceIdentity(
        TelemetryDeviceKind Kind,
        string DeviceKey,
        string DisplayName)
    {
        public static TelemetryDeviceIdentity Cpu(string displayName) =>
            new(TelemetryDeviceKind.Cpu, "cpu", displayName);

        public static TelemetryDeviceIdentity Memory(string displayName) =>
            new(TelemetryDeviceKind.Memory, "memory", displayName);

        /// <summary>
        /// V2-M4.5B Gate C：单根内存模块身份。
        /// <paramref name="moduleKey"/> 是 provider 源本地模块标识（如 memory-module:0）；
        /// 跨 provider 合并由 Reconciler 依据证据决定，provider 不得直接宣布。
        /// </summary>
        public static TelemetryDeviceIdentity MemoryModule(string moduleKey, string displayName) =>
            new(TelemetryDeviceKind.MemoryModule, moduleKey, displayName);

        public static TelemetryDeviceIdentity SystemBoard(string displayName) =>
            new(TelemetryDeviceKind.System, "system", displayName);

        /// <summary>第 index 个 GPU（0 起）；同硬件条件下顺序稳定。</summary>
        public static TelemetryDeviceIdentity GpuByIndex(int index, string displayName) =>
            new(TelemetryDeviceKind.Gpu, $"gpu:{index}", displayName);

        /// <summary>按稳定标识（如盘名 / 官方序号）区分的多存储设备。</summary>
        public static TelemetryDeviceIdentity Storage(string storageKey, string displayName) =>
            new(TelemetryDeviceKind.Storage, $"storage:{storageKey}", displayName);
    }
}
