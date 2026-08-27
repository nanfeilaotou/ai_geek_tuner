using System.Net.Http;

namespace AIGeekTuner.Services.Voice
{
    /// <summary>语音合成的用户侧配置（来自设置快照）。</summary>
    public sealed record VoiceConfiguration(
        string Endpoint,
        string ReferenceAudioPath,
        string PromptText,
        string PromptLang,
        string TextLang,
        double SpeedFactor,
        string? GptModelPath,
        string? SovitsModelPath);

    public sealed record VoiceSynthesisResult(bool Succeeded, byte[]? WavBytes, string? ErrorMessage)
    {
        public static VoiceSynthesisResult Ok(byte[] wav) => new(true, wav, null);
        public static VoiceSynthesisResult Fail(string message) => new(false, null, message);
    }

    public interface IVoiceSynthesisService
    {
        Task<VoiceSynthesisResult> SynthesizeAsync(
            string text,
            VoiceConfiguration configuration,
            CancellationToken cancellationToken);
    }

    /// <summary>Windows 自带 WAV 播放抽象（§43：不引入第三方库）。</summary>
    public interface IWavPlaybackService
    {
        void PlayWav(byte[] wavBytes);
    }
}
