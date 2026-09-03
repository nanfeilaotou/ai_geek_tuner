using System;

namespace AIGeekTuner.Tests.TestSupport;

/// <summary>
/// M4.5E.2：构造最小合法 RIFF/WAVE 头字节（内容本身不是音频，头校验用）。
/// 缓存读取按 RIFF/WAVE 头验证有效性，测试必须使用可被校验接受的字节。
/// </summary>
public static class TestWav
{
    public static byte[] Create()
    {
        var wav = new byte[44];
        wav[0] = (byte)'R';
        wav[1] = (byte)'I';
        wav[2] = (byte)'F';
        wav[3] = (byte)'F';
        wav[8] = (byte)'W';
        wav[9] = (byte)'A';
        wav[10] = (byte)'V';
        wav[11] = (byte)'E';
        return wav;
    }
}
