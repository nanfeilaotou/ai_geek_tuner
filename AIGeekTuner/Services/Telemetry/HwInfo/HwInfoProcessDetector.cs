using System.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    public sealed class HwInfoProcessDetector : IHwInfoProcessDetector
    {
        public bool IsRunning()
        {
            return Process.GetProcessesByName("HWiNFO64").Length > 0
                || Process.GetProcessesByName("HWiNFO32").Length > 0;
        }
    }
}
