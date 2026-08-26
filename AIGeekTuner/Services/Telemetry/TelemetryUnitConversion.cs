using System.Globalization;
using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// Raw → canonical 的单位归一。规则：先转换、后进入 canonical，
    /// 保证同一个 canonical 指标永远只出现一种单位。
    /// </summary>
    public static class TelemetryUnitConversion
    {
        /// <summary>AIDA64 “Used Memory / GPU Used Dedicated Memory” 以 MB 报告；统一到 Byte（1 MB = 1 MiB）。</summary>
        public static double MegabytesToBytes(double megabytes) => megabytes * 1048576d;

        /// <summary>LibreHardwareMonitor Data 类传感器以 GB 报告（LHM 的 GB 即 GiB）；统一到 Byte。</summary>
        public static double GibibytesToBytes(double gibibytes) => gibibytes * 1073741824d;

        public static double GigahertzToMegahertz(double gigahertz) => gigahertz * 1000d;

        public static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>把 WMI 返回的字符串安全解析为有限数值；任何格式异常都返回 false。</summary>
        public static bool TryParseInvariant(string? text, out double value)
        {
            value = double.NaN;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return double.TryParse(
                text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                && IsFinite(value);
        }
    }
}
