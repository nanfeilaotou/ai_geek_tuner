using System;
using System.Globalization;

namespace AIGeekTuner.Services.Hardware.Inventory.Presentation
{
    /// <summary>
    /// V2-M4.5C Gate B：用户可见文本的极小规范化助手。
    /// 只做极少数 obvious alias，不建立大型 vendor database；
    /// 无法识别的值原样保留。
    /// </summary>
    public static class InventoryDisplayText
    {
        /// <summary>CPU 厂商：WMI 原始值 → 用户习惯名（unknown 保留原文）。</summary>
        public static string NormalizeCpuVendor(string? manufacturer) =>
            (manufacturer?.Trim()) switch
            {
                null or "" => string.Empty,
                var m when m.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase) => "Intel",
                var m when m.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase) => "AMD",
                var m => m,
            };

        /// <summary>主板品牌：仅 ASUSTeK 家族规范成 ASUS（obvious alias），其余保留。</summary>
        public static string NormalizeBoardBrand(string? manufacturer)
        {
            var m = manufacturer?.Trim();
            if (string.IsNullOrEmpty(m))
            {
                return string.Empty;
            }

            return m.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase)
                ? "ASUS"
                : m;
        }

        /// <summary>
        /// 统一容量 formatter（GiB 基准）：≥1024 GB 显示 TB（两位小数），
        /// 否则 GB（一位小数）。禁止出现 "3815.4 GB" 这类数字噪音。
        /// </summary>
        public static string FormatCapacity(ulong bytes) =>
            FormatCapacityCore(bytes / 1073741824d);

        public static string FormatCapacity(double gibibytes) =>
            FormatCapacityCore(gibibytes);

        private static string FormatCapacityCore(double gibibytes)
        {
            return gibibytes >= 1024d
                ? string.Create(CultureInfo.InvariantCulture, $"{gibibytes / 1024d:0.##} TB")
                : string.Create(CultureInfo.InvariantCulture, $"{gibibytes:0.#} GB");
        }
    }
}
