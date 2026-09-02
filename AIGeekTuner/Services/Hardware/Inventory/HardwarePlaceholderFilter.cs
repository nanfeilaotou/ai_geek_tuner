using System;
using System.Collections.Generic;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate D：集中式 placeholder sanitizer。OEM 固件经常返回
    /// "To Be Filled By O.E.M." / "Default string" 之类的占位文本——
    /// 统一在这里识别并视为缺失（null），禁止在代码里散落 Contains。
    /// </summary>
    public static class HardwarePlaceholderFilter
    {
        // 小写、去空白后比对。覆盖常见 OEM/AMI/宿主板占位符与显然无意义序列号。
        private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
        {
            "tobefilledbyo.e.m.",
            "tobefilledbyoem",
            "tobefilled",
            "defaultstring",
            "systemserialnumber",
            "chassisserialnumber",
            "baseserialnumber",
            "serialnumber",
            "none",
            "n/a",
            "na",
            "unknown",
            "undefined",
            "oem",
            "o.e.m.",
            "null",
            "0",
            "000000000",
            "0123456789",
            "123456789",
            "xxxxxxxx",
            "type2-boardvendorname1",
            "type2-boardproductname1",
            "notspecified",
            "notavailable",
            "notdefined",
            "empty",
        };

        /// <summary>返回净化后的值：占位符/空白 → null；否则返回原始（已 trim）文本。</summary>
        public static string? Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            var compact = new string(trimmed
                .Where(character => !char.IsWhiteSpace(character))
                .ToArray())
                .ToLowerInvariant();
            return Placeholders.Contains(compact) ? null : trimmed;
        }

        /// <summary>序列号专用：除了通用占位符，全零/全同字符也视为无意义。</summary>
        public static string? SanitizeSerialNumber(string? value)
        {
            var sanitized = Sanitize(value);
            if (sanitized is null)
            {
                return null;
            }

            var compact = sanitized.Where(character => !char.IsWhiteSpace(character)).ToArray();
            if (compact.Length == 0)
            {
                return null;
            }

            var first = compact[0];
            foreach (var character in compact)
            {
                if (character != first)
                {
                    return sanitized;
                }
            }

            return null; // 全同字符（如 "XXXXXXXX"）视为占位。
        }
    }
}
