using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate N 的最小 seam：mapper 消费 IInventoryRow（按键读 WMI 属性），
    /// 测试用 DictionaryInventoryRow 直接构造假记录；
    /// 生产由 WmiInventoryRow 包装 ManagementBaseObject。
    /// 刻意不做 WindowsPlatform mega abstraction。
    /// </summary>
    public interface IInventoryRow
    {
        object? Get(string propertyName);
    }

    public sealed class DictionaryInventoryRow : IInventoryRow
    {
        private readonly IReadOnlyDictionary<string, object?> _values;

        public DictionaryInventoryRow(IReadOnlyDictionary<string, object?> values) => _values = values;

        public object? Get(string propertyName) =>
            _values.TryGetValue(propertyName, out var value) ? value : null;
    }

    public sealed class WmiInventoryRow : IInventoryRow
    {
        private readonly ManagementBaseObject _item;

        public WmiInventoryRow(ManagementBaseObject item) => _item = item;

        public object? Get(string propertyName)
        {
            try
            {
                return _item[propertyName];
            }
            catch (ManagementException)
            {
                return null; // 属性缺失不致命（Gate M best-effort）。
            }
        }
    }

    /// <summary>IInventoryRow 的强类型读取助手（全部容错，失败 → null）。</summary>
    public static class InventoryRowReader
    {
        public static string? String(this IInventoryRow row, string property) =>
            HardwarePlaceholderFilter.Sanitize(
                Convert.ToString(row.Get(property), CultureInfo.InvariantCulture));

        public static string? RawString(this IInventoryRow row, string property) =>
            HardwarePlaceholderFilter.Sanitize(
                Convert.ToString(row.Get(property), CultureInfo.InvariantCulture));

        public static uint? UInt32(this IInventoryRow row, string property)
        {
            var value = row.Get(property);
            if (value is null)
            {
                return null;
            }

            try
            {
                var converted = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                return converted <= uint.MaxValue ? (uint)converted : null;
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
            {
                return null;
            }
        }

        public static ulong? UInt64(this IInventoryRow row, string property)
        {
            var value = row.Get(property);
            if (value is null)
            {
                return null;
            }

            try
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
            {
                return null;
            }
        }

        public static bool? Boolean(this IInventoryRow row, string property)
        {
            var value = row.Get(property);
            if (value is null)
            {
                return null;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException)
            {
                return null;
            }
        }

        public static DateTimeOffset? DateTimeUtc(this IInventoryRow row, string property)
        {
            var value = row.Get(property);
            if (value is null)
            {
                return null;
            }

            try
            {
                if (value is DateTime dateTime)
                {
                    return new DateTimeOffset(dateTime.ToUniversalTime());
                }

                // WMI DMTF 日期：yyyyMMddHHmmss.ffffff±mmm（mmm 为分钟偏移；* 表示本地不确定）。
                var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length < 21)
                {
                    return null;
                }

                if (!DateTime.TryParseExact(
                        text[..21],
                        "yyyyMMddHHmmss.ffffff",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsed))
                {
                    return null;
                }

                var offsetMinutes = 0;
                var suffix = text[21..];
                if (suffix.Length >= 4 && (suffix[0] == '+' || suffix[0] == '-')
                    && int.TryParse(suffix[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
                {
                    offsetMinutes = suffix[0] == '+' ? signed : -signed;
                }

                var offset = TimeSpan.FromMinutes(offsetMinutes);
                return new DateTimeOffset(
                    DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified),
                    offset).ToUniversalTime();
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>WMI ushort[]（WmiMonitorID 的字符数组）→ 字符串（0 结尾截断）。</summary>
        public static string? CharArrayString(this IInventoryRow row, string property)
        {
            var value = row.Get(property);
            if (value is not Array characters || characters.Length == 0)
            {
                return null;
            }

            var builder = new System.Text.StringBuilder(characters.Length);
            foreach (var character in characters)
            {
                var code = Convert.ToUInt32(character, CultureInfo.InvariantCulture);
                if (code == 0)
                {
                    break;
                }

                builder.Append((char)code);
            }

            return HardwarePlaceholderFilter.Sanitize(builder.ToString());
        }
    }
}
