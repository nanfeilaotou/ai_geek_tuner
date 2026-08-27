using System.IO;
using System.Media;

namespace AIGeekTuner.Services.Voice
{
    /// <summary>Windows 自带 WAV 播放（§43）：不引入第三方库。</summary>
    public sealed class SoundPlayerWavPlaybackService : IWavPlaybackService
    {
        public void PlayWav(byte[] wavBytes)
        {
            ArgumentNullException.ThrowIfNull(wavBytes);
            using var stream = new MemoryStream(wavBytes);
            using var player = new SoundPlayer(stream);
            player.PlaySync();
        }
    }
}
