using System;
using System.Linq;
using System.Text;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// 极简 EDID v1 解析（Gate H）：只提取 identity 事实——
    /// PNP 厂商码、产品码、序列号描述符、显示器名描述符。
    /// malformed/截断 EDID 一律返回可用子集或 null，绝不抛异常（Gate M）。
    /// </summary>
    public static class EdidParser
    {
        public sealed record EdidIdentity(
            string? ManufacturerCode,
            string? ProductCode,
            string? SerialNumber,
            string? MonitorName);

        public static EdidIdentity? Parse(byte[]? edid)
        {
            if (edid is null || edid.Length < 18)
            {
                return null;
            }

            // 固定头 00 FF FF FF FF FF FF 00。
            var header = new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
            for (var i = 0; i < header.Length; i++)
            {
                if (edid[i] != header[i])
                {
                    return null;
                }
            }

            return new EdidIdentity(
                ManufacturerCode: DecodeManufacturerCode(edid[8], edid[9]),
                ProductCode: string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "{0:X4}",
                    (ushort)(edid[10] | (edid[11] << 8))),
                SerialNumber: FindDescriptor(edid, 0xFF),
                MonitorName: FindDescriptor(edid, 0xFC));
        }

        /// <summary>EDID 厂商码：两个字节压缩成 3 个 5-bit 字母（如 "ACI"）；'A' = 1。</summary>
        public static string? DecodeManufacturerCode(byte high, byte low)
        {
            var letters = new[]
            {
                (byte)((high >> 2) & 0x1F),
                (byte)((((high & 0x03) << 3) | ((low >> 5) & 0x07)) & 0x1F),
                (byte)(low & 0x1F),
            };
            var builder = new System.Text.StringBuilder(3);
            foreach (var letter in letters)
            {
                if (letter == 0)
                {
                    return null; // 0 不是合法字母。
                }

                builder.Append((char)('A' + letter - 1));
            }

            return builder.Length == 3 ? builder.ToString() : null;
        }

        /// <summary>descriptor 类型 0xFF=序列号、0xFC=显示器名（文本描述符，尾随空白/0x0A 需清除）。</summary>
        private static string? FindDescriptor(byte[] edid, byte descriptorType)
        {
            foreach (var offset in new[] { 54, 72, 90, 108 })
            {
                if (offset + 18 > edid.Length)
                {
                    break;
                }

                if (edid[offset] != 0 || edid[offset + 1] != 0
                    || edid[offset + 2] != 0 || edid[offset + 3] != descriptorType)
                {
                    continue;
                }

                var text = Encoding.ASCII.GetString(edid, offset + 5, 13);
                var trimmed = text.TrimEnd((char)0x0A, (char)0x20, '\0');
                var sanitized = HardwarePlaceholderFilter.Sanitize(trimmed);
                if (sanitized is not null)
                {
                    return sanitized;
                }
            }

            return null;
        }
    }
}
