using System.IO;
using System.Linq;
using System.Text;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Files;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.Files;

/// <summary>
/// FileReaderService 对真实临时文件的读取契约：
/// 四种受支持编码的完整还原、文件边界拒绝策略，以及元数据正确性。
/// </summary>
public class FileReaderServiceTests : IDisposable
{
    private static readonly string SampleLog =
        "[2026-01-01 10:00:00] TM5 Error 2 memory instability\r\n错误代码：2，检测到硬件异常。";

    private const string GbChineseSample =
        "内存测试失败\r\n错误代码：2\r\n检测到硬件异常";

    private readonly FileReaderService _service = new();
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task Read_Utf8WithoutBom_DecodesAndFillsMetadata()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var faultLog = await ReadWrittenAsync("utf8-nobom.log", encoding.GetBytes(SampleLog));

        Assert.Equal(SampleLog, faultLog.Content);
        Assert.Equal("UTF-8", faultLog.EncodingName);
        Assert.Equal("utf8-nobom.log", faultLog.FileName);
        Assert.Equal(FaultLogSourceType.File, faultLog.SourceType);
    }

    [Fact]
    public async Task Read_Utf8WithBom_StripsBomAndDecodes()
    {
        // GetBytes 不携带 BOM，显式拼接 GetPreamble() 构造真实带 BOM 文件。
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(SampleLog)).ToArray();
        var faultLog = await ReadWrittenAsync("utf8-bom.txt", bytes);

        Assert.Equal(SampleLog, faultLog.Content);
        Assert.Equal("UTF-8", faultLog.EncodingName);
    }

    [Fact]
    public async Task Read_Utf16LittleEndianWithBom_DecodesChineseContent()
    {
        var faultLog = await ReadWrittenAsync(
            "utf16le.log",
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetBytes(SampleLog));

        Assert.Equal(SampleLog, faultLog.Content);
        Assert.Equal("UTF-16 LE", faultLog.EncodingName);
    }

    [Fact]
    public async Task Read_Utf16BigEndianWithBom_DecodesChineseContent()
    {
        var faultLog = await ReadWrittenAsync(
            "utf16be.log",
            new UnicodeEncoding(bigEndian: true, byteOrderMark: true).GetBytes(SampleLog));

        Assert.Equal(SampleLog, faultLog.Content);
        Assert.Equal("UTF-16 BE", faultLog.EncodingName);
    }

    [Fact]
    public async Task Read_EmptyFile_ThrowsEmptyFileError()
    {
        var exception = await Record.ExceptionAsync(
            () => ReadWrittenAsync("empty.log", Array.Empty<byte>()));

        Assert.Equal(
            FaultLogReadError.EmptyFile,
            Assert.IsType<FaultLogReadException>(exception).Error);
    }

    [Fact]
    public void Read_FileLargerThanConfiguredLimit_ThrowsFileTooLargeError()
    {
        var strictService = new FileReaderService(new FileReaderOptions
        {
            MaxFileSizeBytes = 8
        });
        var path = _temp.WriteFile("big.log", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        var exception = Assert.Throws<FaultLogReadException>(
            () => strictService.ReadAsync(path).GetAwaiter().GetResult());

        Assert.Equal(FaultLogReadError.FileTooLarge, exception.Error);
    }

    [Fact]
    public void Read_UnsupportedExtension_IsRejectedBeforeRead()
    {
        var path = _temp.WriteFile(
            "notes.md",
            new UTF8Encoding(false).GetBytes("# markdown"));

        var exception = Assert.Throws<FaultLogReadException>(
            () => _service.ReadAsync(path).GetAwaiter().GetResult());

        Assert.Equal(FaultLogReadError.UnsupportedFileType, exception.Error);
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFoundError()
    {
        var missingPath = _temp.Combine("does-not-exist.log");

        var exception = Assert.Throws<FaultLogReadException>(
            () => _service.ReadAsync(missingPath).GetAwaiter().GetResult());

        Assert.Equal(FaultLogReadError.FileNotFound, exception.Error);
    }

    [Fact]
    public void Read_BinaryBytesStartingWithInvalidLead_ThrowsUnsupportedEncodingError()
    {
        // 0xFF 在严格 UTF-8 与常见中文多字节编码中都是非法字节，
        // 且负载不含可触发无 BOM UTF-16 启发式的零字节模式。
        var binary = new byte[]
        {
            0xFF, 0xFB, 0x90, 0x11, 0x22, 0x33, 0x44,
            0x55, 0x66, 0x77, 0x88, 0x99, 0xAA
        };
        var path = _temp.WriteFile("raw.log", binary);

        var exception = Assert.Throws<FaultLogReadException>(
            () => _service.ReadAsync(path).GetAwaiter().GetResult());

        Assert.Equal(FaultLogReadError.UnsupportedEncoding, exception.Error);
    }

    [Fact]
    public async Task Read_WrittenLogFile_FillsFileSizeFromActualBytes()
    {
        var bytes = new UTF8Encoding(false).GetBytes(SampleLog);
        var faultLog = await ReadWrittenAsync("size-check.log", bytes);

        Assert.Equal(bytes.LongLength, faultLog.FileSizeBytes);
        Assert.NotNull(faultLog.CreatedAt);
    }

    [Fact]
    public async Task Read_GbkChineseLog_DecodesViaGb18030Fallback()
    {
        // 注册幂等；GBK(936) 字节序列可被 GB18030 解码器完整还原。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(936).GetBytes(GbChineseSample);

        var faultLog = await ReadWrittenAsync("gbk-ansi.log", bytes);

        Assert.Equal(GbChineseSample, faultLog.Content);
        Assert.Equal("GB18030", faultLog.EncodingName);
    }

    [Fact]
    public async Task Read_Gb18030FourByteCharacter_DecodesCorrectly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var content = "𠀀 rare bmp 外字符\r\nTM5 Error 2";
        var bytes = Encoding.GetEncoding("gb18030").GetBytes(content);

        var faultLog = await ReadWrittenAsync("gb18030.log", bytes);

        Assert.Equal(content, faultLog.Content);
        Assert.Equal("GB18030", faultLog.EncodingName);
    }

    [Fact]
    public async Task Read_AsciiOnlyContent_KeepsUtf8Identification()
    {
        var bytes = new byte[] { 0x41, 0x42, 0x0D, 0x0A, 0x43, 0x44 }; // "AB\r\nCD"

        var faultLog = await ReadWrittenAsync("ascii.log", bytes);

        Assert.Equal("AB\r\nCD", faultLog.Content);
        Assert.Equal("UTF-8", faultLog.EncodingName);
    }

    [Fact]
    public void Read_GbDecodableBytesWithBinaryControlRun_IsStillRejected()
    {
        // 高位字节强制走多字节回退，但夹带的控制字节串使解码结果
        // 不满足文本合理性（控制占比远超阈值），必须继续按二进制拒绝。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = Encoding.GetEncoding("gb18030");
        var binaryJunk =
            gb.GetBytes("内存")
            .Concat(new byte[] { 0x01, 0x02, 0x03, 0x07, 0x08, 0x0B, 0x1F, 0x16 })
            .Concat(gb.GetBytes("测试"))
            .Concat(new byte[] { 0x04, 0x05, 0x06 })
            .ToArray();
        var path = _temp.WriteFile("junk.log", binaryJunk);

        var exception = Assert.Throws<FaultLogReadException>(
            () => _service.ReadAsync(path).GetAwaiter().GetResult());

        Assert.Equal(FaultLogReadError.UnsupportedEncoding, exception.Error);
    }

    [Fact]
    public async Task Read_GbChineseErrorReport_FullyRestored()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(936).GetBytes(GbChineseSample);

        var faultLog = await ReadWrittenAsync("report.log", bytes);

        Assert.Contains("内存测试失败", faultLog.Content);
        Assert.Contains("错误代码：2", faultLog.Content);
        Assert.Contains("检测到硬件异常", faultLog.Content);
    }

    private async Task<FaultLog> ReadWrittenAsync(string fileName, byte[] bytes)
    {
        var path = _temp.WriteFile(fileName, bytes);
        return await _service.ReadAsync(path);
    }

    public void Dispose() => _temp.Dispose();
}
