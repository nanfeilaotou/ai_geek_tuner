using System;
using System.Text.RegularExpressions;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// V2-M4.5B Gate C/D：内存模块（DIMM）传感器命名与标签的事实源。
    ///
    /// Gate D：DDR5 per-module 温度读数的官方语义是 SPD Hub Temperature
    /// （测的是 SPD Hub 芯片，不是 DRAM die 结温）；该原始 label 必须原样保留在
    /// SourceLabel 中，业务代码不得宣传为"DRAM 芯片结温"。
    ///
    /// Gate E：映射必须使用"明确的 parent/device sensor context + exact label"；
    /// 禁止 label.Contains("temperature") 式泛化。模块父传感器命名接受：
    /// HWiNFO 风格 "RAM Module #N" 与 "DIMM N" / "DIMM #N"。
    /// </summary>
    public static partial class MemoryModuleSensorNames
    {
        /// <summary>HWiNFO DDR5 每模块温度读数的官方 label（精确匹配）。</summary>
        public const string SpdHubTemperatureLabel = "SPD Hub Temperature";

        /// <summary>LHM 每模块温度传感器的 label（精确匹配）。</summary>
        public const string ModuleTemperatureLabel = "Temperature";

        // 实测 HWiNFO DDR5 模块传感器命名："DDR5 DIMM [#0] (BANK 0/Controller0-ChannelA-DIMM0)"。
        // 括号内 "/" 之后的部分与 Win32_PhysicalMemory.DeviceLocator 完全一致，
        // 可作为跨源模块身份的强证据（Gate E：有明确 context 才可作为证据）。
        [GeneratedRegex("^(?:RAM Module|DIMM)\\s*#?\\s*(\\d{1,2})$", RegexOptions.IgnoreCase)]
        private static partial Regex ModuleNamePattern();

        [GeneratedRegex("^DDR5 DIMM\\s*\\[#(\\d{1,2})\\]\\s*(?:\\((?<locator>.+)\\))?$", RegexOptions.IgnoreCase)]
        private static partial Regex Ddr5ModuleNamePattern();

        /// <summary>
        /// 父传感器名是否明确描述单根内存模块；成功时给出源本地模块序号
        /// （保留来源自身编号习惯，仅作 source-local identity）。
        /// 接受的命名（全部要求明确的模块 parent，禁止泛化）：
        /// "RAM Module #N" / "DIMM N" / "DIMM #N" / "DDR5 DIMM [#N] (...)"。
        /// </summary>
        public static bool TryGetModuleIndex(string? sensorName, out int moduleIndex)
        {
            moduleIndex = -1;
            if (string.IsNullOrWhiteSpace(sensorName))
            {
                return false;
            }

            var text = sensorName.Trim();
            var simple = ModuleNamePattern().Match(text);
            if (simple.Success
                && int.TryParse(simple.Groups[1].Value, out var parsed))
            {
                moduleIndex = parsed;
                return true;
            }

            var ddr5 = Ddr5ModuleNamePattern().Match(text);
            if (ddr5.Success
                && int.TryParse(ddr5.Groups[1].Value, out var ddr5Index))
            {
                moduleIndex = ddr5Index;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从 DDR5 模块传感器名中提取 SMBIOS DeviceLocator
        /// （如 "DDR5 DIMM [#0] (BANK 0/Controller0-ChannelA-DIMM0)" →
        /// "Controller0-ChannelA-DIMM0"）。该值与 Win32_PhysicalMemory.DeviceLocator
        /// 同源，可作为 reconciliation 强证据；拿不到时返回 false（保守）。
        /// </summary>
        public static bool TryGetModuleDeviceLocator(string? sensorName, out string deviceLocator)
        {
            deviceLocator = string.Empty;
            if (string.IsNullOrWhiteSpace(sensorName))
            {
                return false;
            }

            var match = Ddr5ModuleNamePattern().Match(sensorName.Trim());
            var raw = match.Success ? match.Groups["locator"].Value : null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parts = raw.Split('/');
            var candidate = parts[parts.Length - 1].Trim();
            if (candidate.Length == 0)
            {
                return false;
            }

            deviceLocator = candidate;
            return true;
        }

        /// <summary>温度合理性窗口（复用既有 mapper 的上限语义；不设阈值下限为 0 以外的"安全阈值"）。</summary>
        public static bool IsPlausibleTemperature(double value) =>
            value > 0 && value <= MaxPlausibleTemperatureCelsius;

        private const double MaxPlausibleTemperatureCelsius = 250;
    }
}
