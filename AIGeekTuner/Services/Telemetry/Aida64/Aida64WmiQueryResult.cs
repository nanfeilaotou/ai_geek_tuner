namespace AIGeekTuner.Services.Telemetry.Aida64
{
    public enum Aida64WmiFailureKind
    {
        None = 0,

        /// <summary>root\WMI 下不存在 AIDA64_SensorValues 类（未启用导出或未运行）。</summary>
        ClassMissing = 1,

        /// <summary>其他查询失败；细节只进日志。</summary>
        QueryFailed = 2
    }

    /// <summary>WMI 抓取结果：成功带行，失败带失败类别。</summary>
    public sealed record Aida64WmiQueryResult(
        bool Succeeded,
        Aida64WmiFailureKind FailureKind,
        string? FailureDetail,
        IReadOnlyList<Aida64SensorRow> Rows)
    {
        public static Aida64WmiQueryResult Success(IReadOnlyList<Aida64SensorRow> rows) =>
            new(true, Aida64WmiFailureKind.None, null, rows);

        public static Aida64WmiQueryResult Failure(
            Aida64WmiFailureKind kind,
            string? detail = null) =>
            new(false, kind, detail, []);
    }
}
