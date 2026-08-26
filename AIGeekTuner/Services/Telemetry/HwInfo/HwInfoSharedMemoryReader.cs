using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.HwInfo
{
    /// <summary>
    /// 真实的 HWiNFO Shared Memory 读取器（7.0+ SM2 接口，UTF-8 字符串）。
    ///
    /// 生命周期（§8）：每次 ReadAsync 重新打开当前 mapping → 校验头部 →
    /// 在 mutex（advisory）保护下拷贝所需字节 → 立即释放 → 锁外解析。
    /// HWiNFO 重建 mapping / 12 小时到期停用 → 本次 NotAvailable；
    /// 下一次读取自动恢复，无需重启应用。绝不做任何规避限制的动作。
    ///
    /// 安全解析（§5/§6）：header 声明的 offset/elementSize/count 全部视为不可信输入，
    /// checked 运算 + 边界校验；已知前缀之外的扩展字节安全跳过；坏数据降级为状态而非崩溃。
    /// </summary>
    public sealed class HwInfoSharedMemoryReader : IHwInfoSensorReader
    {
        public const string DefaultMapName = @"Global\HWiNFO_SENS_SM2";

        private const int MaxCopyAttempts = 3;

        private readonly string _mapName;
        private readonly TimeSpan _mutexWaitTimeout;
        private readonly string[] _mutexNames =
        [
            @"Global\HWiNFO_SM2_MUTEX",
            @"Global\HWiNFO_SENS_SM2_MUTEX"
        ];

        public HwInfoSharedMemoryReader(
            string? mapName = null,
            TimeSpan? mutexWaitTimeout = null)
        {
            _mapName = mapName ?? DefaultMapName;
            _mutexWaitTimeout = mutexWaitTimeout ?? TimeSpan.FromMilliseconds(250);
        }

        public async Task<HwInfoReaderOutcome> ReadAsync(CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => ReadCore(cancellationToken), cancellationToken);
        }

        private HwInfoReaderOutcome ReadCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(
                    _mapName,
                    MemoryMappedFileRights.Read,
                    System.IO.HandleInheritability.None);
                using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                // 注意：必须用 Capacity（声明的 section 大小），
                // 而不是视图句柄的 ByteLength（会被页粒度向上取整，导致越界校验失效）。
                var mappedLength = (ulong)Math.Max(0, accessor.Capacity);

                if (mappedLength < HwInfoSharedMemoryLayout.HeaderSize)
                {
                    return HwInfoReaderOutcome.SharedMemoryNotAvailable("mapping 小于最小头部尺寸。");
                }

                // ---- mutex：advisory。HWiNFO 常以提升权限运行，拿不到锁也要能读；
                //      拿到锁时只在“拷贝”这一小段同步代码内持有。----
                using Mutex? mutex = TryOpenMutex();
                bool held = false;
                if (mutex is not null)
                {
                    try
                    {
                        held = mutex.WaitOne((int)_mutexWaitTimeout.TotalMilliseconds);
                    }
                    catch (AbandonedMutexException)
                    {
                        held = true; // 前任（如崩溃的 HWiNFO）未释放；等待已成功获得所有权
                    }
                }

                try
                {
                    return CopyAndParseWithRetry(accessor, mappedLength, cancellationToken);
                }
                finally
                {
                    if (held)
                    {
                        mutex?.ReleaseMutex();
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return HwInfoReaderOutcome.SharedMemoryNotAvailable("共享内存不存在。");
            }
            catch (UnauthorizedAccessException exception)
            {
                return HwInfoReaderOutcome.ReadFailed($"共享内存访问被拒绝：{exception.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "Telemetry/HwInfo reader");
                return HwInfoReaderOutcome.ReadFailed(exception.Message);
            }
        }

        private HwInfoReaderOutcome CopyAndParseWithRetry(
            MemoryMappedViewAccessor accessor,
            ulong mappedLength,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryReadHeader(accessor, out var header))
                {
                    return HwInfoReaderOutcome.SharedMemoryNotAvailable(
                        "签名不匹配——mapping 可能正在重建、已被停用或不是 HWiNFO SM2 数据。");
                }

                if (!TryValidateSections(header, mappedLength, out var sensorCount, out var readingCount, out var problem))
                {
                    return attempt < MaxCopyAttempts - 1
                        ? SpinBackoff(attempt)
                        : HwInfoReaderOutcome.SharedMemoryNotAvailable(problem);
                }

                var sensorBytes = new byte[sensorCount * HwInfoSharedMemoryLayout.SensorPrefixSize];
                var readingBytes = new byte[readingCount * HwInfoSharedMemoryLayout.ReadingPrefixSize];
                CopyElements(accessor, header.SensorOffset, header.SensorStride, sensorCount,
                    HwInfoSharedMemoryLayout.SensorPrefixSize, sensorBytes);
                CopyElements(accessor, header.ReadingOffset, header.ReadingStride, readingCount,
                    HwInfoSharedMemoryLayout.ReadingPrefixSize, readingBytes);

                // 防撕裂：拷贝后重读 header，确认描述的仍是刚才那份内容。
                if (TryReadHeader(accessor, out var after) && Describes(header, after))
                {
                    if (header.Version < HwInfoSharedMemoryLayout.MinVersion)
                    {
                        return HwInfoReaderOutcome.SharedMemoryNotAvailable(
                            $"接口版本过旧（v{header.Version}），需要 v{HwInfoSharedMemoryLayout.MinVersion}+。");
                    }

                    // PollTime 是 Unix 秒（官方语义）。全零/荒谬时间戳说明这是
                    // 稀疏页、残留段或已停用映射——拒绝而不是把 1970 年当真。
                    var pollUtc = DateTimeOffset.FromUnixTimeSeconds(header.PollTime);
                    var now = DateTimeOffset.UtcNow;
                    if (pollUtc < now.AddYears(-6) || pollUtc > now.AddHours(36))
                    {
                        return HwInfoReaderOutcome.SharedMemoryNotAvailable(
                            "PollTime 不在合理范围内（映射可能已被停用或为稀疏页）。");
                    }

                    return Parse(header, sensorBytes, readingBytes, sensorCount, readingCount);
                }

                if (attempt >= MaxCopyAttempts - 1)
                {
                    return HwInfoReaderOutcome.ReadFailed("HWiNFO 在多次读取期间持续更新 section。");
                }

                System.Threading.Thread.SpinWait(200 << attempt);
            }
        }

        private static HwInfoReaderOutcome SpinBackoff(int attempt)
        {
            System.Threading.Thread.SpinWait(200 << attempt);
            return HwInfoReaderOutcome.SharedMemoryNotAvailable("section 校验未通过。");
        }

        private bool TryReadHeader(
            MemoryMappedViewAccessor accessor,
            out (uint Signature, uint Version, uint Revision, long PollTime,
                uint SensorOffset, uint SensorStride, uint SensorCount,
                uint ReadingOffset, uint ReadingStride, uint ReadingCount) header)
        {
            header = default;
            var signature = accessor.ReadUInt32(0);
            if (signature != HwInfoSharedMemoryLayout.Signature)
            {
                return false;
            }

            header = (
                signature,
                accessor.ReadUInt32(4),
                accessor.ReadUInt32(8),
                accessor.ReadInt64(12),
                accessor.ReadUInt32(20),
                accessor.ReadUInt32(24),
                accessor.ReadUInt32(28),
                accessor.ReadUInt32(32),
                accessor.ReadUInt32(36),
                accessor.ReadUInt32(40));
            return true;
        }

        private static bool TryValidateSections(
            (uint Signature, uint Version, uint Revision, long PollTime,
             uint SensorOffset, uint SensorStride, uint SensorCount,
             uint ReadingOffset, uint ReadingStride, uint ReadingCount) header,
            ulong mappedLength,
            out int sensorCount,
            out int readingCount,
            out string problem)
        {
            sensorCount = 0;
            readingCount = 0;

            if (!TryValidateSection(
                    header.SensorOffset, header.SensorStride, header.SensorCount,
                    HwInfoSharedMemoryLayout.SensorPrefixSize, mappedLength,
                    out sensorCount, out problem))
            {
                return false;
            }

            if (!TryValidateSection(
                    header.ReadingOffset, header.ReadingStride, header.ReadingCount,
                    HwInfoSharedMemoryLayout.ReadingPrefixSize, mappedLength,
                    out readingCount, out problem))
            {
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool TryValidateSection(
            uint offset, uint stride, uint count, int prefixSize,
            ulong mappedLength,
            out int elementCount,
            out string problem)
        {
            elementCount = 0;
            if (stride < (uint)prefixSize)
            {
                problem = $"元素步长 {stride} 小于已知最小前缀 {prefixSize}。";
                return false;
            }

            ulong end;
            try
            {
                end = checked(offset + stride * count); // uint*uint 在 checked 下不会静默回绕
            }
            catch (OverflowException)
            {
                problem = "section 范围计算溢出（count 过大）。";
                return false;
            }

            if (end > mappedLength)
            {
                problem = $"section 结束于 {end}，超出映射长度 {mappedLength}。";
                return false;
            }

            if (count > (uint)(int.MaxValue / prefixSize))
            {
                problem = $"元素数量 {count} 过大。";
                return false;
            }

            elementCount = (int)count;
            problem = string.Empty;
            return true;
        }

        /// <summary>按 header 步长逐元素只拷贝已知前缀；扩展字节原地跳过（§6 前向兼容）。</summary>
        private static void CopyElements(
            MemoryMappedViewAccessor accessor,
            uint sectionOffset,
            uint stride,
            int count,
            int prefixSize,
            byte[] destination)
        {
            for (var i = 0; i < count; i++)
            {
                var sourceOffset = sectionOffset + (ulong)((long)i * stride);
                for (var b = 0; b < prefixSize; b++)
                {
                    destination[(i * prefixSize) + b] = accessor.ReadByte((long)(sourceOffset + (ulong)b));
                }
            }
        }

        private static bool Describes(
            (uint Signature, uint Version, uint Revision, long PollTime,
             uint SensorOffset, uint SensorStride, uint SensorCount,
             uint ReadingOffset, uint ReadingStride, uint ReadingCount) first,
            (uint Signature, uint Version, uint Revision, long PollTime,
             uint SensorOffset, uint SensorStride, uint SensorCount,
             uint ReadingOffset, uint ReadingStride, uint ReadingCount) second) =>
            first.PollTime == second.PollTime
            && first.SensorOffset == second.SensorOffset
            && first.SensorStride == second.SensorStride
            && first.SensorCount == second.SensorCount
            && first.ReadingOffset == second.ReadingOffset
            && first.ReadingStride == second.ReadingStride
            && first.ReadingCount == second.ReadingCount;

        private HwInfoReaderOutcome Parse(
            (uint Signature, uint Version, uint Revision, long PollTime,
             uint SensorOffset, uint SensorStride, uint SensorCount,
             uint ReadingOffset, uint ReadingStride, uint ReadingCount) header,
            byte[] sensorBytes,
            byte[] readingBytes,
            int sensorCount,
            int readingCount)
        {
            var sensors = new List<HwInfoSensorEntry>(sensorCount);
            var sensorMetaByIdx = new List<(uint Id, uint Instance)>(sensorCount);
            for (var i = 0; i < sensorCount; i++)
            {
                var offset = i * HwInfoSharedMemoryLayout.SensorPrefixSize;
                var id = BitConverter.ToUInt32(sensorBytes, offset + HwInfoSharedMemoryLayout.SensorIdOffset);
                var instance = BitConverter.ToUInt32(sensorBytes, offset + HwInfoSharedMemoryLayout.SensorInstanceOffset);
                var nameOrig = DecodeField(sensorBytes, offset + HwInfoSharedMemoryLayout.SensorNameOrigOffset,
                    HwInfoSharedMemoryLayout.StringFieldLength);
                var nameUser = DecodeField(sensorBytes, offset + HwInfoSharedMemoryLayout.SensorNameUserOffset,
                    HwInfoSharedMemoryLayout.StringFieldLength);
                sensors.Add(new HwInfoSensorEntry((uint)i, nameOrig.Length > 0 ? nameOrig : nameUser));
                sensorMetaByIdx.Add((id, instance));
            }

            var readings = new List<HwInfoReadingEntry>(readingCount);
            for (var i = 0; i < readingCount; i++)
            {
                var offset = i * HwInfoSharedMemoryLayout.ReadingPrefixSize;
                var sensorIndex = BitConverter.ToUInt32(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingSensorIndexOffset);
                if (sensorIndex >= (uint)sensorCount)
                {
                    continue; // 孤儿读数：坏数据跳过单条而不是整体失败
                }

                var type = BitConverter.ToUInt32(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingTypeOffset);
                var readingId = BitConverter.ToUInt32(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingIdOffset);
                var labelOrig = DecodeField(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingLabelOrigOffset,
                    HwInfoSharedMemoryLayout.StringFieldLength);
                var labelUser = DecodeField(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingLabelUserOffset,
                    HwInfoSharedMemoryLayout.StringFieldLength);
                var unit = DecodeField(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingUnitOffset,
                    HwInfoSharedMemoryLayout.UnitFieldLength);
                var value = BitConverter.ToDouble(readingBytes, offset + HwInfoSharedMemoryLayout.ReadingValueOffset);

                readings.Add(new HwInfoReadingEntry(
                    sensorIndex,
                    readingId,
                    labelOrig.Length > 0 ? labelOrig : labelUser,
                    unit,
                    value,
                    type));
            }

            var versionText = $"SHM v{header.Version}.{header.Revision}";

            // 空载荷防线：counts>0 但所有名称与标签皆空 → 稀疏页/已停用映射。
            if (sensorCount > 0
                && sensors.All(entry => entry.SensorName.Length == 0)
                && readings.All(entry => entry.Label.Length == 0))
            {
                return HwInfoReaderOutcome.SharedMemoryNotAvailable("映射载荷为空（可能已停用或正在重建）。");
            }

            return HwInfoReaderOutcome.Snapshot(sensors, readings, versionText);
        }

        /// <summary>
        /// NUL 截断的定长字符串：严格 UTF-8 优先（7.33+ 官方编码），
        /// 解码失败回退 Latin1（旧版兼容）；截断/非法字节都不会让快照失败（§7）。
        /// </summary>
        public static string DecodeField(byte[] buffer, int offset, int length)
        {
            var end = offset + length;
            var actual = end;
            for (var i = offset; i < end; i++)
            {
                if (buffer[i] == 0)
                {
                    actual = i;
                    break;
                }
            }

            if (actual == offset)
            {
                return string.Empty;
            }

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true)
                    .GetString(buffer, offset, actual - offset);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(buffer, offset, actual - offset);
            }
        }

        private Mutex? TryOpenMutex()
        {
            foreach (var name in _mutexNames)
            {
                if (Mutex.TryOpenExisting(name, out var mutex))
                {
                    return mutex;
                }
            }

            return null;
        }
    }
}
