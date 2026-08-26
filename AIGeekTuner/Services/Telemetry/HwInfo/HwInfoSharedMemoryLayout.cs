namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>
    /// HWiNFO Shared Memory Interface（SM2）布局常量。
    ///
    /// 事实来源（V2-M1.1 Gate A）：
    /// - 官方：thread 7092（7.0 起 SHM 接口细节完全公开、无实现限制）；
    ///   v7.33-4905 Beta 官方公告（UTF-8 扩展，正确客户端无需改动即可继续工作）。
    /// - 官方规范帖当前需登录；以下字段经两个互相独立的公开实现交叉验证一致：
    ///   Seraksab/HWiNFO.SharedMemory.Net（MIT，C#，含真实机捕获 fixture）
    ///   与 mkullber/HWiNFO-RTSS（Python，struct 格式串逐字节吻合）。
    ///
    /// 契约要点：头部自带各 section 的 offset / elementSize / count；
    /// 新版本只允许追加字段 —— 已知前缀之外的扩展字节必须跳过而不是报错。
    /// </summary>
    internal static class HwInfoSharedMemoryLayout
    {
        public const int HeaderSize = 44;

        /// <summary>"HWiS" 小端。HWiNFO 在拆除 section 时会覆写它（如 0xDEADBEEF）。</summary>
        public const uint Signature = 0x53695748;

        /// <summary>低于 2 的接口版本不支持（官方自 7.x 起为 2+，新版本只追加字段）。</summary>
        public const uint MinVersion = 2;

        public const int SensorPrefixSize = 264;      // id(4)+instance(4)+name[128]+user[128]
        public const int ReadingPrefixSize = 316;     // type(4)+sensorIdx(4)+id(4)+label[128]
                                                      // +labelUser[128]+unit[16]+value*4(double)

        // Sensor 元素内偏移
        public const int SensorIdOffset = 0;
        public const int SensorInstanceOffset = 4;
        public const int SensorNameOrigOffset = 8;
        public const int SensorNameUserOffset = 136;

        // Reading 元素内偏移
        public const int ReadingTypeOffset = 0;
        public const int ReadingSensorIndexOffset = 4;
        public const int ReadingIdOffset = 8;
        public const int ReadingLabelOrigOffset = 12;
        public const int ReadingLabelUserOffset = 140;
        public const int ReadingUnitOffset = 268;
        public const int ReadingValueOffset = 284;
        public const int ReadingValueMinOffset = 292;
        public const int ReadingValueMaxOffset = 300;
        public const int ReadingValueAvgOffset = 308;

        public const int StringFieldLength = 128;
        public const int UnitFieldLength = 16;
    }
}
