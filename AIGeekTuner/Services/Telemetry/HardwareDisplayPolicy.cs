using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// V2-M3.3 普通用户 Hardware 页显示策略（只影响展示层，不改 Telemetry 底层语义）。
    ///
    /// 规则：
    /// A) resolved canonical 物理设备一律可显示；
    /// B) 未合并的单源设备仅当来源给出了真实设备名（归一后非空）才可显示；
    ///    匿名占位名（GPU #n / HDD #n / 裸类别名）绝不进主界面——
    ///    它们仍完整保留在 Raw 明细（数据源详情对话框）中。
    ///
    /// 绝不为了减少重复而猜测设备归属：Reconciler 未给出证据的 AIDA 匿名 GPU
    /// （Temperature/Hotspot 看起来像 dGPU 也一样）不得挂到具名显卡卡片上，
    /// 宁可主页面暂时不显示这些指标。CPU/内存/主板是天然单一逻辑实体，
    /// 不做名称筛选（显示标题由调用方规范化为“CPU”/“内存”等）。
    /// </summary>
    public static class HardwareDisplayPolicy
    {
        public static bool IsUserVisible(TelemetryDeviceIdentity device)
        {
            ArgumentNullException.ThrowIfNull(device);

            return device.Kind switch
            {
                TelemetryDeviceKind.Cpu => true,
                TelemetryDeviceKind.Memory => true,
                TelemetryDeviceKind.System => true,
                // Storage 匿名设备允许进入装配器，由 §5 的“磁盘 #N”回退规则决定；
                // GPU 匿名设备无回退——宁可少显示，不可错误归属（§2/§3）。
                TelemetryDeviceKind.Storage => true,
                _ => TelemetryDeviceReconciler.NormalizeName(device.DisplayName).Length > 0,
            };
        }
    }
}
