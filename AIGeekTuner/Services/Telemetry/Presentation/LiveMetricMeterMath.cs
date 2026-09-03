using System;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry.Presentation
{
    /// <summary>meter 几何（全部为 0..1 的归一化位置；null = 不绘制该 marker）。</summary>
    public sealed record LiveMeterGeometry(
        double NormalizedFillEnd,
        double NormalizedCurrent,
        double? NormalizedLow,
        double? NormalizedHigh);

    /// <summary>
    /// V2-M4.5C Gate E/G：meter 的纯几何计算。
    /// visual scale 只是显示标尺（usage 0-100、CPU/GPU 温度 0-110、内存/盘温 0-100），
    /// 不是安全阈值；超出 scale 的值 clamp marker 到末端，数字仍显示真实值。
    /// 不产生任何"健康等级/颜色状态"概念（禁止红黄绿）。
    /// </summary>
    public static class LiveMetricMeterMath
    {
        public static readonly double UsageScaleMax = 100;
        public static readonly double CpuGpuTemperatureScaleMax = 110;
        public static readonly double MemoryStorageTemperatureScaleMax = 100;

        public static LiveMeterGeometry Compute(
            double scaleMin, double scaleMax,
            double current, double? low, double? high)
        {
            if (scaleMax <= scaleMin)
            {
                throw new ArgumentOutOfRangeException(nameof(scaleMax),
                    "scaleMax must be greater than scaleMin.");
            }

            var currentNorm = Clamp01(Normalize(scaleMin, scaleMax, current));
            double? lowNorm = low.HasValue ? Clamp01(Normalize(scaleMin, scaleMax, low.Value)) : null;
            double? highNorm = high.HasValue ? Clamp01(Normalize(scaleMin, scaleMax, high.Value)) : null;
            return new LiveMeterGeometry(currentNorm, currentNorm, lowNorm, highNorm);
        }

        /// <summary>marker 排序恒成立：low ≤ high（clamp 后仍保持；相等允许重叠）。</summary>
        public static bool MarkersOrdered(LiveMeterGeometry geometry) =>
            (!geometry.NormalizedLow.HasValue || !geometry.NormalizedHigh.HasValue)
            || geometry.NormalizedLow.Value <= geometry.NormalizedHigh.Value + 1e-9;

        private static double Normalize(double min, double max, double value) =>
            (value - min) / (max - min);

        private static double Clamp01(double value) =>
            value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
