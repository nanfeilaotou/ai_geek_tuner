namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>
    /// Provider 自己确定并声明的源侧设备事实（§13 第一层）。
    /// 这是确定事实；能否与其他来源的设备合并成同一物理硬件，
    /// 由 Reconciler 依据证据决定，Provider 无权直接宣布。
    /// </summary>
    public sealed record SourceDeviceInfo(
        TelemetrySourceKind Source,
        TelemetryDeviceKind Kind,
        string NativeDeviceId,
        string NativeDeviceName,
        int NativeOrdinal,
        IReadOnlyList<string> StrongIds)
    {
        public static SourceDeviceInfo Create(
            TelemetrySourceKind source,
            TelemetryDeviceKind kind,
            string nativeDeviceId,
            string nativeDeviceName,
            int nativeOrdinal,
            params string[] strongIds) =>
            new(source, kind, nativeDeviceId, nativeDeviceName, nativeOrdinal, strongIds);
    }
}
