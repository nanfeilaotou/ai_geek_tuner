using System.IO;
using AIGeekTuner.Services.Voice;
using Xunit;

namespace AIGeekTuner.Tests.Services.Voice
{
    /// <summary>§45 缓存复用/失效 + §15 播放抽象字节传递。</summary>
    public class VoiceCacheAndPlaybackTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "agt-voice-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private static VoiceConfiguration Config(string speed = "1.0") => new(
            "http://127.0.0.1:9880",
            "D:/gsv/ref.wav",
            "prompt", "ja", "zh",
            double.Parse(speed, System.Globalization.CultureInfo.InvariantCulture),
            null, null);

        [Fact]
        public void Cache_MissWhenEmpty_HitAfterWrite_InvalidatedByTextOrConfig()
        {
            var cache = new SpokenSummaryCache(_dir);
            var wav = new byte[] { 1, 2, 3 };

            Assert.False(cache.TryRead("文本", Config(), out _));

            cache.Write("文本", Config(), wav);
            Assert.True(cache.TryRead("文本", Config(), out var hit));
            Assert.Equal(wav, hit);

            // 文本变化 → 失效
            Assert.False(cache.TryRead("新文本", Config(), out _));

            // 速度变化 → 失效（写入 0.8 后旧速度配置不再命中）
            cache.Write("文本", Config("0.8"), wav);
            Assert.False(cache.TryRead("文本", Config(), out _));
        }

        [Fact]
        public void Playback_ReceivesExactBytes()
        {
            byte[]? received = null;
            IWavPlaybackService player = new FakePlayer(b => received = b);

            player.PlayWav([9, 8, 7]);

            Assert.Equal(new byte[] { 9, 8, 7 }, received);
        }

        private sealed class FakePlayer(Action<byte[]> onPlay) : IWavPlaybackService
        {
            public void PlayWav(byte[] wavBytes) => onPlay(wavBytes);
        }
    }
}
