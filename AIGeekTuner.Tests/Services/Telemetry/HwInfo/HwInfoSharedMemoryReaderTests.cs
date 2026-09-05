using System.Text;
using AIGeekTuner.Services.Telemetry.HwInfo;

namespace AIGeekTuner.Tests.Services.Telemetry.HwInfo
{
    /// <summary>§26 HWiNFO parser 边界：header 全部字段按不可信输入处理。</summary>
    public class HwInfoSharedMemoryReaderTests
    {
        // Test-only mapping: Local avoids requiring SeCreateGlobalPrivilege under a normal user.
        // Production reader semantics remain Global\HWiNFO_SENS_SM2.
        private const string TestMapName = @"Local\AIGEEKTUNER_TEST_SHM";
        private int _mapSequence;

        private HwInfoMappingFixture DefaultFixture()
        {
            var fixture = new HwInfoMappingFixture();
            fixture.Sensors.Add((1, 0, "CPU [#0]: Intel Core", ""));
            fixture.Sensors.Add((2, 0, "GPU [#0]: NVIDIA GeForce RTX 4080", ""));
            fixture.Readings.Add((1, 0, 10, "CPU Package", "", "\u00b0C", 71.4));
            fixture.Readings.Add((6, 0, 11, "Core Clocks", "", "MHz", 4200));
            fixture.Readings.Add((7, 1, 20, "GPU Utilization", "", "%", 96));
            return fixture;
        }

        private async Task<HwInfoReaderOutcome> ReadOnce(HwInfoMappingFixture fixture)
        {
            var mapName = TestMapName + (_mapSequence++);
            using var live = new HwInfoMappingFixture.LiveMapping(mapName, fixture.Build());
            var reader = new HwInfoSharedMemoryReader(mapName);
            return await reader.ReadAsync();
        }

        // ---- 正常路径 ----

        [Fact]
        public async Task ValidMapping_ParsesSensorsAndReadings()
        {
            var outcome = await ReadOnce(DefaultFixture());

            Assert.True(outcome.Available);
            Assert.Equal(2, outcome.Sensors.Count);
            Assert.Equal(3, outcome.Readings.Count);
            var cpuTemp = outcome.Readings.First(r => r.ReadingId == 10);
            Assert.Equal(71.4, cpuTemp.Value);
            Assert.Equal("\u00b0C", cpuTemp.Unit);
            Assert.NotNull(outcome.SourceVersion);
            Assert.StartsWith("SHM v", outcome.SourceVersion);
        }

        [Fact]
        public async Task ExtendedElementStride_KnownPrefixRead_ExtensionSkipped()
        {
            var fixture = DefaultFixture();
            fixture.ReadingExtraBytes = 64;   // 未来版本追加字段的情形
            fixture.SensorExtraBytes = 32;

            var outcome = await ReadOnce(fixture);

            Assert.True(outcome.Available);
            Assert.Equal(3, outcome.Readings.Count);
            Assert.Equal(96, outcome.Readings.First(r => r.ReadingId == 20).Value);
        }

        [Fact]
        public async Task Utf8ChineseDeviceName_DecodedCorrectly()
        {
            var fixture = new HwInfoMappingFixture();
            fixture.Sensors.Add((9, 0, "\u82f1\u4f01\u5c14 \u6838\u663e", ""));
            fixture.Readings.Add((1, 0, 1, "\u6838\u5fc3\u6e29\u5ea6", "", "\u2103", 55.5));

            var outcome = await ReadOnce(fixture);

            Assert.True(outcome.Available);
            Assert.Contains("\u6838\u663e", outcome.Sensors[0].SensorName, StringComparison.Ordinal);
            Assert.Equal("\u6838\u5fc3\u6e29\u5ea6", outcome.Readings[0].Label);
        }

        [Fact]
        public async Task NonUtf8LegacyBytes_FallBackToLatin1_WithoutFailingSnapshot()
        {
            var fixture = DefaultFixture();
            var bytes = fixture.Build();
            // 在 unit 字段写入非法 UTF-8 序列（Latin1 的 °=0xB0 单字节）
            var readingBase = 44 + (264 * 2) + (316 * 0) + 268;
            bytes[readingBase] = 0xB0;
            bytes[readingBase + 1] = 0x43; // "°C" in Latin1
            bytes[readingBase + 2] = 0x00; // 终止符（覆盖原 UTF-8 的残留字节）
            var mapName = TestMapName + (_mapSequence++);
            using var live = new HwInfoMappingFixture.LiveMapping(mapName, bytes);
            var reader = new HwInfoSharedMemoryReader(mapName);

            var outcome = await reader.ReadAsync();

            Assert.True(outcome.Available); // 快照不崩
            Assert.Equal("\u00b0C", outcome.Readings[0].Unit); // 回退解码得到 °C
        }

        [Fact]
        public async Task TruncatedUtf8SequenceAtFieldBoundary_DoesNotCrash()
        {
            var fixture = new HwInfoMappingFixture();
            fixture.Sensors.Add((3, 0, "\u82f1\u4f01\u5c14", "")); // 中文（多字节结尾）
            var bytes = fixture.Build();
            // 把 nameOrig 的最后一个有效字节之后直接截断成 0xFF（非法起始字节）
            bytes[44 + 8 + 12] = 0xFF;

            var mapName = TestMapName + (_mapSequence++);
            using var live = new HwInfoMappingFixture.LiveMapping(mapName, bytes);
            var reader = new HwInfoSharedMemoryReader(mapName);

            var outcome = await reader.ReadAsync();

            Assert.True(outcome.Available); // 不抛异常
            Assert.NotEmpty(outcome.Sensors[0].SensorName); // 回退产生替代文本
        }

        // ---- 坏头部 / 坏 section ----

        [Fact]
        public async Task InvalidSignature_ReportedUnavailable()
        {
            var fixture = DefaultFixture();
            fixture.Signature = 0x12345678;

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available);
            Assert.Null(outcome.FailureDetail);
        }

        [Fact]
        public async Task TornDownSignature_ReportedUnavailable_NotCrash()
        {
            var fixture = DefaultFixture();
            fixture.Signature = 0xDEADBEEF; // 官方拆除行为

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available,
                $"unexpectedly available: reason={outcome.UnavailableReason}");
        }

        [Fact]
        public async Task UnsupportedOldVersion_Rejected()
        {
            var fixture = DefaultFixture();
            fixture.Version = 1;

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available);
            Assert.Null(outcome.FailureDetail);
        }

        [Fact]
        public async Task TruncatedHeader_MappingTooSmall_Unavailable()
        {
            var fixture = DefaultFixture();
            fixture.SkipPayload = true;
            fixture.TotalLengthOverride = 20; // 小于最小头部长度

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available);
        }

        [Fact]
        public async Task SensorOffsetOutOfBounds_CorruptReported_NoException()
        {
            var fixture = DefaultFixture();
            fixture.SkipPayload = true;
            fixture.SensorOffsetOverride = uint.MaxValue - 100;

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available);
        }

        [Fact]
        public async Task ReadingSectionBeyondLength_CorruptReported()
        {
            var fixture = DefaultFixture();
            fixture.SkipPayload = true;
            fixture.ReadingOffsetOverride = 100_000;

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available,
                $"unexpectedly available: reason={outcome.UnavailableReason}");
        }

        [Fact]
        public async Task ElementCountOverflow_CheckedArithmetic_NoHangNoCrash()
        {
            var fixture = DefaultFixture();
            fixture.SkipPayload = true;
            fixture.TotalLengthOverride = 44;
            fixture.SensorCountOverride = uint.MaxValue;

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available);
        }

        [Fact]
        public async Task ElementSizeBelowKnownMinimum_Rejected()
        {
            var fixture = DefaultFixture();
            fixture.SkipPayload = true;
            fixture.ReadingStrideOverride = 100;

            var outcome = await ReadOnce(fixture);

            Assert.False(outcome.Available);
        }

        [Fact]
        public async Task OrphanReading_SensorIndexOutOfRange_SkipsSingleReading()
        {
            var fixture = DefaultFixture();
            fixture.Readings.Add((1, 99, 77, "Ghost", "", "\u00b0C", 1));

            var outcome = await ReadOnce(fixture);

            Assert.True(outcome.Available); // 整体不失败
            Assert.DoesNotContain(outcome.Readings, r => r.ReadingId == 77);
            Assert.Equal(3, outcome.Readings.Count);
        }

        [Fact]
        public void DecodeField_HandlesAsciiUtf8AndInvalidBytes()
        {
            var buffer = new byte[16];
            Array.Copy(new byte[] { (byte)'A', (byte)'B' }, buffer, 2);
            Assert.Equal("AB", HwInfoSharedMemoryReader.DecodeField(buffer, 0, 16));

            var utf8 = new byte[] { 0xE4, 0xB8, 0xAD, 0x00 }; // 中
            Assert.Equal("\u4e2d", HwInfoSharedMemoryReader.DecodeField(utf8, 0, 4));

            var invalid = new byte[] { 0xFF, 0xFE, 0x41 };
            Assert.Equal(
                Encoding.Latin1.GetString(invalid),
                HwInfoSharedMemoryReader.DecodeField(invalid, 0, 3));

            Assert.Equal(string.Empty, HwInfoSharedMemoryReader.DecodeField(new byte[8], 0, 8));
        }

        // ---- 生命周期 ----

        [Fact]
        public async Task MappingMissing_ReportsUnavailable()
        {
            var reader = new HwInfoSharedMemoryReader(@"Global\AIGEEKTUNER_NEVER_EXISTS_XYZ");

            var outcome = await reader.ReadAsync();

            Assert.False(outcome.Available);
            Assert.Null(outcome.FailureDetail);
        }

        [Fact]
        public async Task MappingDisappearThenReturn_StatusRecoversAcrossReads()
        {
            var mapName = TestMapName + "_lifecycle_" + (_mapSequence++);
            var fixture = DefaultFixture();

            using (var first = new HwInfoMappingFixture.LiveMapping(mapName, fixture.Build()))
            {
                var reader = new HwInfoSharedMemoryReader(mapName);
                var ok = await reader.ReadAsync();
                Assert.True(ok.Available);
            } // mapping 消失

            var gone = await new HwInfoSharedMemoryReader(mapName).ReadAsync();
            Assert.False(gone.Available);

            using (var second = new HwInfoMappingFixture.LiveMapping(mapName, fixture.Build()))
            {
                var back = await new HwInfoSharedMemoryReader(mapName).ReadAsync();
                Assert.True(back.Available); // 下一次读取自动恢复，无需重启应用
            }
        }

        [Fact]
        public async Task Cancellation_BeforeRead_ThrowsOperationCanceled()
        {
            var reader = new HwInfoSharedMemoryReader(TestMapName + "_cancel");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.ReadAsync(cts.Token));
        }
    }
}
