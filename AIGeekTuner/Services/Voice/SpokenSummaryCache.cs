using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIGeekTuner.Services.Voice
{
    /// <summary>
    /// SpokenSummary WAV 缓存（§45 方案 A）：文本+语音配置完全一致则复用，
    /// 否则重新生成并覆盖。meta.json 记录指纹要素，不做复杂哈希库。
    /// </summary>
    public sealed class SpokenSummaryCache
    {
        private sealed record CacheMeta(string Text, string Endpoint, string ReferenceAudioPath,
            string PromptText, string PromptLang, double SpeedFactor);

        private readonly string _directory;

        public SpokenSummaryCache(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        private string WavPath => Path.Combine(_directory, "spoken-summary.wav");
        private string MetaPath => Path.Combine(_directory, "spoken-summary.meta.json");

        private static string MetaJson(string text, VoiceConfiguration config) =>
            JsonSerializer.Serialize(new CacheMeta(
                text, config.Endpoint, config.ReferenceAudioPath,
                config.PromptText, config.PromptLang, config.SpeedFactor));

        public bool TryRead(string text, VoiceConfiguration config, out byte[] wav)
        {
            wav = [];
            if (!File.Exists(WavPath) || !File.Exists(MetaPath))
            {
                return false;
            }

            try
            {
                var stored = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(MetaPath));
                if (stored is null || stored.Text != text
                    || stored.Endpoint != config.Endpoint
                    || stored.ReferenceAudioPath != config.ReferenceAudioPath
                    || stored.PromptText != config.PromptText
                    || stored.PromptLang != config.PromptLang
                    || Math.Abs(stored.SpeedFactor - config.SpeedFactor) > 0.001)
                {
                    return false;
                }

                wav = File.ReadAllBytes(WavPath);
                return wav.Length > 0;
            }
            catch
            {
                return false; // meta 损坏视为未命中
            }
        }

        public void Write(string text, VoiceConfiguration config, byte[] wav)
        {
            File.WriteAllBytes(WavPath, wav);
            File.WriteAllText(MetaPath, MetaJson(text, config));
        }
    }
}
