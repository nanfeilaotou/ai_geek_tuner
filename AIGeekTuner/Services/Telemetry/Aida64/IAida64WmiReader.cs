namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>
    /// 原始 WMI 抓取 seam：把“如何拿到行”与“行意味着什么”分离，
    /// 测试用确定性 fixture 替换本接口，绝不构造通用 WMI 框架抽象。
    /// </summary>
    public interface IAida64WmiReader
    {
        Aida64WmiQueryResult Query();
    }
}
