using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    /// <summary>analysis.json 信封：deterministic session.json 保持稳定，AI 结果独立持久化（§24/§25）。</summary>
    public sealed record SessionAnalysisEnvelope(
        int SchemaVersion,
        string SessionId,
        DateTimeOffset AnalyzedAtUtc,
        string ModelName,
        long DurationMs,
        bool RepairUsed,
        SessionAnalysisResult Result,
        string ContextJson);

    public interface ISessionAnalysisStore
    {
        void Save(SessionAnalysisEnvelope envelope);

        SessionAnalysisEnvelope? Load(string sessionId);

        /// <summary>轻量存在性检查（只 File.Exists，不反序列化）——历史列表“已分析”标记用。</summary>
        bool AnalysisExists(string sessionId);

        /// <summary>M4.5E.2 Gate G：语音缓存只读事实源——存在且 RIFF/WAVE 头有效。</summary>
        bool HasCachedVoice(string sessionId);

        string VoiceWavPathOf(string sessionId);

        bool VoiceWavExists(string sessionId);

        void SaveVoiceWav(string sessionId, byte[] wavBytes);

        byte[]? TryLoadVoiceWav(string sessionId);
    }

    public sealed class SessionAnalysisStore : ISessionAnalysisStore
    {
        private const int SchemaVersion = 1;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _rootDirectory;

        public SessionAnalysisStore(string sessionsDirectory)
        {
            _rootDirectory = sessionsDirectory;
            Directory.CreateDirectory(_rootDirectory);
        }

        // ---- V2-M4.5D：会话级语音缓存（Sessions/{id}/voice.wav）——
        // 分析完成即预生成；播放时命中缓存则不再调用 GPT-SoVITS。

        public string VoiceWavPathOf(string sessionId) =>
            Path.Combine(_rootDirectory, sessionId, "voice.wav");

        public bool VoiceWavExists(string sessionId) =>
            File.Exists(VoiceWavPathOf(sessionId));

        public void SaveVoiceWav(string sessionId, byte[] wavBytes)
        {
            ArgumentNullException.ThrowIfNull(wavBytes);
            var directory = Path.Combine(_rootDirectory, sessionId);
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, "voice.wav.tmp");
            File.WriteAllBytes(temp, wavBytes);
            File.Move(temp, VoiceWavPathOf(sessionId), overwrite: true);
        }

        public byte[]? TryLoadVoiceWav(string sessionId)
        {
            var file = VoiceWavPathOf(sessionId);
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(file);
                // M4.5E.2 Gate K-14：损坏/空文件按缓存未命中处理（播放时会重新合成）。
                if (bytes.Length < 12 || !IsRiffWavHeader(bytes))
                {
                    return null;
                }

                return bytes;
            }
            catch (System.Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis voice load");
                return null;
            }
        }

        public void Save(SessionAnalysisEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var directory = Path.Combine(_rootDirectory, envelope.SessionId);
            Directory.CreateDirectory(directory);
            var wrapped = new EnvelopeFile(SchemaVersion, envelope);
            var temp = Path.Combine(directory, "analysis.json.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(wrapped, SerializerOptions));
            File.Move(temp, Path.Combine(directory, "analysis.json"), overwrite: true);
        }

        public SessionAnalysisEnvelope? Load(string sessionId)
        {
            var file = Path.Combine(_rootDirectory, sessionId, "analysis.json");
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var wrapped = JsonSerializer.Deserialize<EnvelopeFile>(File.ReadAllText(file), SerializerOptions);
                return wrapped?.Analysis;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis load");
                return null;
            }
        }

        /// <summary>M4.5E.1 补充：历史列表“已分析/未分析”标记——只查文件存在，绝不触发分析。</summary>
        public bool AnalysisExists(string sessionId) =>
            File.Exists(Path.Combine(_rootDirectory, sessionId, "analysis.json"));

        /// <summary>
        /// M4.5E.2 Gate G/H：语音缓存的只读事实源——存在且 RIFF/WAVE 头有效。
        /// 直接 File.Exists + 头校验定位（确定性文件名，无 index、无 lazy 初始化），
        /// 重启后第一次查询即读真实磁盘状态。analysis 存在 ≠ 语音存在。
        /// </summary>
        public bool HasCachedVoice(string sessionId)
        {
            var file = VoiceWavPathOf(sessionId);
            if (!File.Exists(file))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(file);
                if (stream.Length < 12)
                {
                    return false;
                }

                var header = new byte[12];
                stream.ReadExactly(header);
                return IsRiffWavHeader(header);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis voice cache probe");
                return false;
            }
        }

        private static bool IsRiffWavHeader(ReadOnlySpan<byte> header) =>
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';

        private sealed record EnvelopeFile(int SchemaVersion, SessionAnalysisEnvelope Analysis);
    }
}
