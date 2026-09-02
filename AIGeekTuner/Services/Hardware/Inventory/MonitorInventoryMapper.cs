using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate H：Monitor inventory。identity = root\wmi WmiMonitorID（EDID 派生的
    /// 厂商码/产品码/序列号/友好名）+ WmiGetMonitorRawEEdidV1Block 原始 EDID 补充；
    /// 物理尺寸 = WmiMonitorBasicDisplayParams；当前分辨率/刷新率/主屏 =
    /// GDI 枚举（按 EDID PNP id "ACI24D3" 式键匹配到显示器）。
    /// 不做显示控制/DDC/布局修改；多显示器逐条 best-effort，单条异常不拖垮整体。
    /// </summary>
    public static class MonitorInventoryMapper
    {
        public sealed record DisplayModeDescriptor(
            string DeviceName,
            string? MonitorDeviceId,
            int Width,
            int Height,
            double RefreshRateHz,
            bool IsPrimary,
            bool IsActive);

        public sealed record RawEdidEntry(string Identity, byte[] Edid);

        public static IReadOnlyList<MonitorInventoryInfo> Map(
            IReadOnlyList<IInventoryRow> monitorIds,
            IReadOnlyList<IInventoryRow> displayParams,
            IReadOnlyList<RawEdidEntry> rawEdids,
            IReadOnlyList<DisplayModeDescriptor> displayModes)
        {
            var paramsByInstance = displayParams
                .Select(row => (Identity: row.String("InstanceName"), Row: row))
                .Where(entry => entry.Identity is not null)
                .GroupBy(entry => entry.Identity!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.OrdinalIgnoreCase);

            var modesByPnpId = displayModes
                .Where(mode => mode.IsActive)
                .Select(mode => (PnpId: ExtractPnpId(mode.MonitorDeviceId), Mode: mode))
                .Where(entry => entry.PnpId is not null)
                .GroupBy(entry => entry.PnpId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Mode, StringComparer.OrdinalIgnoreCase);

            var results = new List<MonitorInventoryInfo>(monitorIds.Count);
            foreach (var row in monitorIds)
            {
                try
                {
                    var identity = row.String("InstanceName");
                    var manufacturerCode = row.CharArrayString("ManufacturerName");
                    var productCode = row.CharArrayString("ProductCodeId");
                    var serial = row.CharArrayString("SerialNumberId");
                    var friendlyName = row.CharArrayString("UserFriendlyName");
                    var year = row.UInt32("YearOfManufacture");

                    // 原始 EDID：identity 是 InstanceName 的稳定子串即认匹配。
                    var edid = FindEdid(rawEdids, identity);
                    if (edid is not null)
                    {
                        var parsed = EdidParser.Parse(edid);
                        if (parsed is not null)
                        {
                            manufacturerCode ??= parsed.ManufacturerCode;
                            productCode ??= parsed.ProductCode;
                            serial ??= parsed.SerialNumber;
                            friendlyName ??= parsed.MonitorName;
                        }
                    }

                    uint? widthCm = null;
                    uint? heightCm = null;
                    if (identity is not null && paramsByInstance.TryGetValue(identity, out var paramRow))
                    {
                        widthCm = paramRow.UInt32("MaxHorizontalImageSize");
                        heightCm = paramRow.UInt32("MaxVerticalImageSize");
                    }

                    var pnpKey = BuildPnpKey(manufacturerCode, productCode);
                    var mode = pnpKey is not null && modesByPnpId.TryGetValue(pnpKey, out var found)
                        ? found
                        : null;

                    results.Add(new MonitorInventoryInfo(
                        FriendlyName: friendlyName,
                        ManufacturerCode: manufacturerCode,
                        ProductCode: productCode,
                        SerialNumber: HardwarePlaceholderFilter.SanitizeSerialNumber(serial),
                        YearOfManufacture: year,
                        PhysicalWidthCm: widthCm,
                        PhysicalHeightCm: heightCm,
                        DiagonalInches: EstimateDiagonal(widthCm, heightCm),
                        CurrentResolution: mode is null ? null : $"{mode.Width} × {mode.Height}",
                        CurrentRefreshRateHz: mode?.RefreshRateHz,
                        IsPrimary: mode?.IsPrimary,
                        EdidIdentity: identity,
                        Source: InventorySource.WmiMonitor));
                }
                catch (Exception)
                {
                    // 单个显示器行异常 → 跳过该行，不让整体失败（Gate M）。
                }
            }

            return results;
        }

        /// <summary>GDI monitor DeviceID 形如 "MONITOR\ACI24D3\{...}"；截取 PNP id。</summary>
        public static string? ExtractPnpId(string? monitorDeviceId)
        {
            if (string.IsNullOrWhiteSpace(monitorDeviceId))
            {
                return null;
            }

            // 兼容两种 DeviceID 形态：MONITOR\ACI24D3\{...}（设备名树）与
            // \\?\DISPLAY#BOE0B35#...（EDD_GET_DEVICE_INTERFACE_NAME，# 分隔）。
            // 统一提取 3 字母 + 4 位十六进制的 EDID PNP id（如 ACI24D3 / BOE0B35）。
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                monitorDeviceId,
                "(?<=[\\\\#])[A-Za-z]{3}[0-9A-Fa-f]{4}"))
            {
                return match.Value;
            }

            return null;
        }

        internal static string? BuildPnpKey(string? manufacturerCode, string? productCode) =>
            string.IsNullOrWhiteSpace(manufacturerCode) || string.IsNullOrWhiteSpace(productCode)
                ? null
                : manufacturerCode + productCode;

        internal static double? EstimateDiagonal(uint? widthCm, uint? heightCm)
        {
            if (!widthCm.HasValue || !heightCm.HasValue || widthCm.Value == 0 || heightCm.Value == 0)
            {
                return null;
            }

            var diagonalCm = Math.Sqrt(
                (double)widthCm.Value * widthCm.Value + (double)heightCm.Value * heightCm.Value);
            return Math.Round(diagonalCm / 2.54, 1);
        }

        private static byte[]? FindEdid(IReadOnlyList<RawEdidEntry> rawEdids, string? identity)
        {
            if (identity is null)
            {
                return null;
            }

            foreach (var entry in rawEdids)
            {
                if (identity.Contains(entry.Identity, StringComparison.OrdinalIgnoreCase)
                    || entry.Identity.Contains(identity, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Edid;
                }
            }

            return null;
        }
    }
}
