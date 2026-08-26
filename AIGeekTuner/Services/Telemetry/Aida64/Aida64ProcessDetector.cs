using System.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.Aida64
{
    /// <summary>AIDA64 主程序进程检测（aida64.exe）。</summary>
    public sealed class Aida64ProcessDetector : IAida64ProcessDetector
    {
        public bool IsRunning()
        {
            // 图吧工具箱等便携分发也使用相同可执行名。
            return Process.GetProcessesByName("aida64").Length > 0;
        }
    }
}
