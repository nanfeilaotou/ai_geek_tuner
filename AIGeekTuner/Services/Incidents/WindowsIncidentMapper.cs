using AIGeekTuner.Models.Incidents;

namespace AIGeekTuner.Services.Incidents
{
    /// <summary>
    /// RawWindowsEvent → WindowsIncident 映射（Gate C/E）。
    ///
    /// 映射纪律：
    /// - 只接受“Provider + Event ID”的受限组合（WHEA 是 provider 级识别），
    ///   绝不用 message 文本分类；
    /// - 未命中任何规则的事件直接忽略（返回 null），宁缺毋滥；
    /// - Kernel-Power 41 只有“未正常关机”语义，绝不解释为电源/CPU/PSU 故障。
    ///
    /// 归一化：
    /// - 时间一律 UTC；severity 由 Event Level（1=Critical,2=Error,3=Warning）映射；
    /// - message 格式化失败 → “Provider（事件 N）”回退文本；
    /// - Summary ≤ 300 字符，Details ≤ 2000 字符（不吞几十 KB XML）。
    /// </summary>
    public static class WindowsIncidentMapper
    {
        private const int MaxSummaryLength = 300;

        public const int MaxDetailsLength = 2000;

        public static WindowsIncident? Map(RawWindowsEvent raw)
        {
            ArgumentNullException.ThrowIfNull(raw);

            var category = MapCategory(raw.ProviderName, raw.EventId);
            if (category is null)
            {
                return null;
            }

            var message = string.IsNullOrWhiteSpace(raw.Message)
                ? FallbackSummary(raw.ProviderName, raw.EventId)
                : raw.Message.Trim();

            return new WindowsIncident(
                OccurredAtUtc: raw.TimeCreatedUtc.ToUniversalTime(),
                Category: category.Value,
                Severity: MapSeverity(raw.Level),
                ProviderName: raw.ProviderName,
                EventId: raw.EventId,
                Channel: raw.Channel,
                RecordId: raw.RecordId,
                Summary: Truncate(message, MaxSummaryLength),
                Details: raw.Message is null ? null : Truncate(raw.Message.Trim(), MaxDetailsLength),
                EvidenceId: string.Empty); // 由 source 层按最终排序统一编号
        }

        public static IncidentSeverity MapSeverity(int? level) => level switch
        {
            1 => IncidentSeverity.Critical,
            2 => IncidentSeverity.Error,
            3 => IncidentSeverity.Warning,
            _ => IncidentSeverity.Information, // 0/4/未知 vendor level 一律 Information
        };

        public static string FallbackSummary(string providerName, int eventId) =>
            $"{providerName}（事件 {eventId}）";

        /// <summary>Provider + EventId → Category；返回 null 表示该事件不构成 incident 证据。</summary>
        public static IncidentCategory? MapCategory(string providerName, int eventId)
        {
            switch (providerName)
            {
                case "Kernel-Power" when eventId == 41:
                    return IncidentCategory.UnexpectedShutdown;

                case "EventLog" when eventId == 6008:
                    // “先前一次系统关闭是意外的”——经典的非正常关机证据。
                    return IncidentCategory.UnexpectedShutdown;

                case "Microsoft-Windows-WER-SystemErrorReporting" when eventId == 1001:
                    // BugCheck 报告入口（蓝屏发生后的 WER 记录）。
                    return IncidentCategory.BugCheck;

                case "Microsoft-Windows-WHEA-Logger":
                    // 硬件错误专用 provider；M4.1 不解析 binary payload。
                    return IncidentCategory.HardwareError;

                case "Display" when eventId == 4101:
                    // TDR：显示驱动停止响应并已恢复。
                    return IncidentCategory.DisplayDriver;

                case "disk" when eventId is 7 or 51 or 153:
                    // disk 7=坏块, 51=分页操作错误, 153=意外 IO 失败。
                    return IncidentCategory.Storage;

                case "Application Error" when eventId == 1000:
                    return IncidentCategory.ApplicationCrash;

                case "Application Hang" when eventId == 1002:
                    return IncidentCategory.ApplicationHang;

                case "Windows Error Reporting" when eventId == 1001:
                    return IncidentCategory.WindowsErrorReporting;

                default:
                    return null;
            }
        }

        public static string Truncate(string text, int maxLength) =>
            text.Length <= maxLength ? text : text[..maxLength];
    }
}
