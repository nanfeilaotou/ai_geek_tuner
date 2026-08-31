namespace AIGeekTuner.Models.Incidents
{
    /// <summary>
    /// Windows Incident 类别（M4.1 受限集合）。
    /// 只有“Provider + Event ID 的可靠官方语义”才允许映射到具体类别；
    /// 禁止按 message 文本猜测分类。无法可靠归类的事件不进入结果（Gate C）。
    /// </summary>
    public enum IncidentCategory
    {
        UnexpectedShutdown,
        BugCheck,
        HardwareError,
        DisplayDriver,
        Storage,
        ApplicationCrash,
        ApplicationHang,
        WindowsErrorReporting,
        Other,
    }

    /// <summary>严重级别：优先直接映射 Windows EventRecord Level，不自造概念。</summary>
    public enum IncidentSeverity
    {
        Information,
        Warning,
        Error,
        Critical,
    }

    /// <summary>
    /// 一条归一化的 Windows 事件证据。不保存 raw XML、不保存完整异常栈；
    /// Summary/Details 是有界文本（Details 上限 2000 字符，见 Mapper）。
    /// </summary>
    public sealed record WindowsIncident(
        DateTimeOffset OccurredAtUtc,
        IncidentCategory Category,
        IncidentSeverity Severity,
        string ProviderName,
        int EventId,
        string Channel,
        long? RecordId,
        string Summary,
        string? Details,
        string EvidenceId);

    /// <summary>
    /// 一次有界事件查询。窗口与结果数都有 hard cap（Gate C）：
    /// 默认绝不扫整个 System/Application 历史。
    /// </summary>
    public sealed record IncidentQuery(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int MaxResults)
    {
        public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);

        /// <summary>
        /// 边界容差：“最近 24 小时”类查询在两次 UtcNow 采样之间必然漂移出
        /// 几毫秒，1 秒容差让合法的满窗查询不误拒（25 小时仍会拒绝）。
        /// </summary>
        public static readonly TimeSpan WindowBoundaryTolerance = TimeSpan.FromSeconds(1);

        public const int MaxResultsCap = 500;

        public void Validate()
        {
            if (StartUtc >= EndUtc)
            {
                throw new ArgumentException("查询窗口无效：StartUtc 必须早于 EndUtc。");
            }

            if (EndUtc - StartUtc > MaxWindow + WindowBoundaryTolerance)
            {
                throw new ArgumentException($"查询窗口过大：最大允许 {MaxWindow.TotalHours:0} 小时。");
            }

            if (MaxResults is < 1 or > MaxResultsCap)
            {
                throw new ArgumentException($"MaxResults 必须在 1 到 {MaxResultsCap} 之间。");
            }
        }
    }

    /// <summary>单个 channel 的查询结果状态（Gate F：失败隔离，不整单失败）。</summary>
    public enum IncidentQueryStatus
    {
        Success,
        Partial,
        Unavailable,
        PermissionDenied,
        Error,
    }

    /// <summary>一个 channel 的只读结果摘要（用户级，不包含异常细节）。</summary>
    public sealed record IncidentChannelResult(
        string Channel,
        IncidentQueryStatus Status,
        string? StatusMessage,
        int IncidentCount);

    /// <summary>合并后的查询结果：按时间升序、去重、EvidenceId 稳定（incident:0001 起）。</summary>
    public sealed record IncidentQueryResult(
        IReadOnlyList<WindowsIncident> Incidents,
        IReadOnlyList<IncidentChannelResult> Channels)
    {
        public IncidentQueryStatus Status { get; init; } = IncidentQueryStatus.Success;
    }
}
