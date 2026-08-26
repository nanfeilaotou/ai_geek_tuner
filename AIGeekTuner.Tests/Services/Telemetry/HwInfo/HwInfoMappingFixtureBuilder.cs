using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using AIGeekTuner.Services.Telemetry.HwInfo;

namespace AIGeekTuner.Tests.Services.Telemetry.HwInfo
{
    /// <summary>
    /// 按交叉验证过的 SM2 布局合成 mapping 字节；所有字段可注入坏值以测试解析器边界。
    /// </summary>
    public sealed class HwInfoMappingFixture
    {
        public uint Signature = 0x53695748;             // "HWiS"
        public uint Version = 2;
        public uint Revision = 1;
        public long PollTime = 1700000000;

        public List<(uint Id, uint Instance, string NameOrig, string NameUser)> Sensors = [];
        public List<(uint Type, uint SensorIdx, uint ReadingId, string Label, string LabelUser, string Unit, double Value)> Readings = [];

        public uint? SensorOffsetOverride;
        public uint? SensorStrideOverride;
        public uint? SensorCountOverride;
        public uint? ReadingOffsetOverride;
        public uint? ReadingStrideOverride;
        public uint? ReadingCountOverride;
        public int SensorExtraBytes;
        public int ReadingExtraBytes;
        public int? TotalLengthOverride;

        /// <summary>头部声明非法（offset/count 越界）时跳过 payload 写入，只保留 header 字节。</summary>
        public bool SkipPayload;

        public const int HeaderSize = 44;
        public const int SensorPrefix = 264;
        public const int ReadingPrefix = 316;

        public byte[] Build()
        {
            var sensorStride = SensorStrideOverride ?? (uint)(SensorPrefix + SensorExtraBytes);
            var readingStride = ReadingStrideOverride ?? (uint)(ReadingPrefix + ReadingExtraBytes);
            var sensorCount = (uint)Sensors.Count;
            var readingCount = (uint)Readings.Count;
            var sensorOffset = SensorOffsetOverride ?? HeaderSize;
            var readingOffset = ReadingOffsetOverride ??
                (uint)(HeaderSize + sensorStride * Math.Max(Sensors.Count, SensorCountOverride ?? 0));

            var total = TotalLengthOverride ??
                (int)Math.Max(
                    readingOffset + readingStride * Math.Max(Readings.Count, ReadingCountOverride ?? 0),
                    sensorOffset + sensorStride * Math.Max(Sensors.Count, SensorCountOverride ?? 0));

            var buffer = new byte[Math.Max(total, 44)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), Signature);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), Revision);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(12), PollTime);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), sensorOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), sensorStride);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28), SensorCountOverride ?? sensorCount);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), readingOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36), readingStride);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), ReadingCountOverride ?? readingCount);
            if (SkipPayload)
            {
                return buffer;
            }

            WriteElements(
                buffer,
                (int)sensorOffset,
                (int)sensorStride,
                Sensors.Count,
                element =>
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan((int)sensorOffset + (element * (int)sensorStride), 4), Sensors[element].Id);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan((int)sensorOffset + (element * (int)sensorStride) + 4, 4), Sensors[element].Instance);
                    WriteString(buffer,
                        (int)sensorOffset + (element * (int)sensorStride) + 8,
                        Sensors[element].NameOrig);
                    WriteString(buffer,
                        (int)sensorOffset + (element * (int)sensorStride) + 136,
                        Sensors[element].NameUser);
                });

            for (var i = 0; i < Readings.Count; i++)
            {
                var baseOffset = (int)readingOffset + (i * (int)readingStride);
                var r = Readings[i];
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(baseOffset + 0, 4), r.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(baseOffset + 4, 4), r.SensorIdx);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(baseOffset + 8, 4), r.ReadingId);
                WriteString(buffer, baseOffset + 12, r.Label);
                WriteString(buffer, baseOffset + 140, r.LabelUser);
                WriteString(buffer, baseOffset + 268, r.Unit, maxBytes: 16);
                BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(baseOffset + 284, 8), r.Value);
            }

            return buffer;
        }

        private static void WriteElements(byte[] buffer, int offset, int stride, int count, Action<int> fill)
        {
            for (var i = 0; i < count; i++)
            {
                fill(i);
            }
        }

        internal static void WriteString(byte[] buffer, int offset, string value, int maxBytes = 128)
        {
            var bytes = new UTF8Encoding(false).GetBytes(value);
            var count = Math.Min(bytes.Length, maxBytes - 1);
            Array.Copy(bytes, 0, buffer, offset, count);
            buffer[offset + count] = 0;
        }

        /// <summary>把字节写入真实命名 OS 共享内存，返回可 Dispose 的控制柄。</summary>
        public sealed class LiveMapping : IDisposable
        {
            private readonly MemoryMappedFile _mmf;

            public LiveMapping(string mapName, byte[] bytes)
            {
                _mmf = MemoryMappedFile.CreateNew(mapName, bytes.Length);
                using var accessor = _mmf.CreateViewAccessor();
                accessor.Write(0, (ulong)bytes.LongLength);
                accessor.Flush();
                using var stream = _mmf.CreateViewStream();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            public void Dispose() => _mmf.Dispose();
        }
    }
}
