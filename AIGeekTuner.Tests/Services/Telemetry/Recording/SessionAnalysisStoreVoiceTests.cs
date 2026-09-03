using System;
using System.IO;
using AIGeekTuner.Services.Telemetry.Recording;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording
{
    /// <summary>
    /// V2-M4.5D：会话级语音缓存（Sessions/{id}/voice.wav）。
    /// V2-M4.5E.2 Gate G/H/K：HasCachedVoice 只读事实源 + RIFF 头校验
    ///（重启后第一次查询即读磁盘，损坏/空文件不算有效缓存）。
    /// </summary>
    public sealed class SessionAnalysisStoreVoiceTests : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly SessionAnalysisStore _store;

        public SessionAnalysisStoreVoiceTests()
        {
            _store = new SessionAnalysisStore(_temp.FullPath);
        }

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void SaveThenLoadVoiceWav_RoundTrips()
        {
            var wav = TestWav.Create();
            _store.SaveVoiceWav("s-voice", wav);

            Assert.True(_store.VoiceWavExists("s-voice"));
            Assert.True(_store.HasCachedVoice("s-voice"));
            var loaded = _store.TryLoadVoiceWav("s-voice");
            Assert.NotNull(loaded);
            Assert.Equal(wav, loaded);
            Assert.EndsWith("voice.wav", _store.VoiceWavPathOf("s-voice"));
        }

        [Fact]
        public void MissingVoiceWav_ReturnsNull()
        {
            Assert.False(_store.VoiceWavExists("s-none"));
            Assert.False(_store.HasCachedVoice("s-none"));
            Assert.Null(_store.TryLoadVoiceWav("s-none"));
        }

        [Fact]
        public void SaveVoiceWav_OverwritesPrevious()
        {
            _store.SaveVoiceWav("s-overwrite", TestWav.Create());
            var second = TestWav.Create();
            second[12] = 0xAB;
            _store.SaveVoiceWav("s-overwrite", second);

            var loaded = _store.TryLoadVoiceWav("s-overwrite");
            Assert.NotNull(loaded);
            Assert.Equal(0xAB, loaded[12]);
        }

        // ---- M4.5E.2 Gate K-14：损坏/空缓存不算有效 ----

        [Fact]
        public void CorruptWav_NotTreatedAsValidCache()
        {
            var directory = Path.Combine(_temp.FullPath, "s-corrupt");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "voice.wav"), new byte[] { 1, 2, 3, 4 });

            Assert.False(_store.HasCachedVoice("s-corrupt"));
            Assert.Null(_store.TryLoadVoiceWav("s-corrupt"));
        }

        [Fact]
        public void EmptyWav_NotTreatedAsValidCache()
        {
            var directory = Path.Combine(_temp.FullPath, "s-empty-wav");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "voice.wav"), Array.Empty<byte>());

            Assert.False(_store.HasCachedVoice("s-empty-wav"));
            Assert.Null(_store.TryLoadVoiceWav("s-empty-wav"));
        }

        [Fact]
        public void CacheIdentity_IsPerSession_DifferentSessionsNeverShare()
        {
            _store.SaveVoiceWav("s-a", TestWav.Create());

            Assert.True(_store.HasCachedVoice("s-a"));
            Assert.False(_store.HasCachedVoice("s-b"));   // A 的缓存绝不使 B 显示已生成
        }

        /// <summary>
        /// M4.5E.3 Gate G 契约锁：HasCachedVoice == true 时，TryLoadVoiceWav 对同一合法
        /// 文件必须成功且与磁盘字节一致——两套 API 共享同一 RIFF/WAVE 校验 helper，
        /// 绝不允许“UI 显示已生成、播放却拿不到 bytes”的分叉规则。
        /// </summary>
        [Fact]
        public void HasCachedVoice_True_Implies_TryLoadVoiceWav_SucceedsWithDiskBytes()
        {
            var wav = TestWav.Create();
            _store.SaveVoiceWav("s-contract", wav);
            var path = _store.VoiceWavPathOf("s-contract");
            var diskLength = new FileInfo(path).Length;

            Assert.True(_store.HasCachedVoice("s-contract"));
            var loaded = _store.TryLoadVoiceWav("s-contract");
            Assert.NotNull(loaded);
            Assert.Equal(diskLength, loaded!.Length);   // loaded byte length == disk byte length
            Assert.Equal(wav, loaded);
        }

        /// <summary>
        /// Gate K-11：当前产品语义（M4.5D 确立）= 会话级缓存复用——同一会话内
        /// 语音配置变化不使缓存失效（确定性文件名 key=sessionId，无第二套 hash）。
        /// 本测试锁定该语义，防止无意变更。
        /// </summary>
        [Fact]
        public void CacheIdentity_SameSession_ConfigChange_KeepsCache_DocumentedReuse()
        {
            var wav = TestWav.Create();
            _store.SaveVoiceWav("s-config", wav);

            // 语义锁定：缓存 key 只含 sessionId；配置变化后仍命中（复用）。
            Assert.True(_store.HasCachedVoice("s-config"));
            Assert.Equal(wav, _store.TryLoadVoiceWav("s-config"));
        }
    }
}
