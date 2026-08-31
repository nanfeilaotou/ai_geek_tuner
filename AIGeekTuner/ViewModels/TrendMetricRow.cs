using AIGeekTuner.Controls;

namespace AIGeekTuner.ViewModels
{
    /// <summary>趋势行：当前/最小/最大 + 最近值序列（供 Sparkline 渲染）。</summary>
    /// <param name="FullName">完整设备名，仅用于 Tooltip（§10：行内用短名）。</param>
    public sealed class TrendMetricRow(
        string label,
        string current,
        string min,
        string max,
        IReadOnlyList<double> points,
        string fullName)
    {
        public string Label { get; } = label;
        public string Current { get; } = current;
        public string Min { get; } = min;
        public string Max { get; } = max;
        public IReadOnlyList<double> Points { get; } = points;
        public string FullName { get; } = fullName;
    }
}
