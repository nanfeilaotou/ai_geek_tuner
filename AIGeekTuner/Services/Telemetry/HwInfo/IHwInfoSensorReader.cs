namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>
    /// HWiNFO 共享内存读取器 seam。
    /// M1 授权范围说明：官方接口规范当前仍按开发者条件分发（2011 官方政策帖 +
    /// 登录墙后的 v7.33 UTF-8 扩展文档），未能确认“完全公开”。
    /// 因此本仓库不包含任何布局解析实现；未来在许可确认或采用官方 SDK 后，
    /// 以独立实现替换 <see cref="HwInfoSharedMemoryUnavailableReader"/> 即可接入。
    /// </summary>
    public interface IHwInfoSensorReader
    {
        Task<HwInfoReaderOutcome> ReadAsync(CancellationToken cancellationToken = default);
    }
}
