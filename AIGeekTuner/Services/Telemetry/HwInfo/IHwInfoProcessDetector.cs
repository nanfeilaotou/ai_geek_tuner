namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>检测 HWiNFO 是否正在运行（HWiNFO64.exe / HWiNFO32.exe）。</summary>
    public interface IHwInfoProcessDetector
    {
        bool IsRunning();
    }
}
