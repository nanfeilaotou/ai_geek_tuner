namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>检测 AIDA64 是否正在运行；用于区分“未运行”与“运行但未启用导出”。</summary>
    public interface IAida64ProcessDetector
    {
        bool IsRunning();
    }
}
