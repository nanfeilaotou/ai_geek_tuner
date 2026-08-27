using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIGeekTuner.Services.Voice
{
    /// <summary>
    /// GPT-SoVITS api_v2 客户端：POST /tts（media_type=wav，streaming_mode=false）。
    /// 用户显式提供模型路径时先经官方 /set_gpt_weights、/set_sovits_weights 下发。
    /// 响应守卫（§42）：成功状态 + 音频类 Content-Type + 非空 + 大小上限。
    /// </summary>
    public sealed class GptSoVitsVoiceSynthesisService : IVoiceSynthesisService
    {
        public const int MaxResponseBytes = 25 * 1024 * 1024;

        /// <summary>中文等非 ASCII 直接以 UTF-8 输出，便于日志排查与服务器解析。</summary>
        private static readonly JsonSerializerOptions TtsJsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly HttpClient _httpClient;

        // 同进程权重去重（§14）：配置未变则跳过 set_*_weights；TTS 失败后允许重发一次。
        private string? _lastAppliedGptPath;
        private string? _lastAppliedSovitsPath;

        public GptSoVitsVoiceSynthesisService(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
        }

        public async Task<VoiceSynthesisResult> SynthesizeAsync(
            string text,
            VoiceConfiguration configuration,
            CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = configuration.Endpoint.TrimEnd('/');

                var switchError = await SwitchModelsIfConfiguredAsync(endpoint, configuration, cancellationToken)
                    .ConfigureAwait(false);
                if (switchError is not null)
                {
                    return VoiceSynthesisResult.Fail(switchError);
                }

                var payload = JsonSerializer.Serialize(
                    new
                    {
                        text,
                        text_lang = "zh",
                        ref_audio_path = configuration.ReferenceAudioPath,
                        prompt_text = configuration.PromptText ?? string.Empty,
                        prompt_lang = configuration.PromptLang,
                        speed_factor = configuration.SpeedFactor,
                        media_type = "wav",
                        streaming_mode = false,
                    },
                    TtsJsonOptions);

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(endpoint + "/tts", content, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return VoiceSynthesisResult.Fail("HTTP " + (int)response.StatusCode + ": " + Truncate(body, 200));
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (mediaType.Length > 0
                    && !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    && !mediaType.Contains("octet-stream", StringComparison.Ordinal))
                {
                    return VoiceSynthesisResult.Fail("响应不是音频（Content-Type=" + mediaType + "）。");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    return VoiceSynthesisResult.Fail("响应音频为空。");
                }

                if (bytes.Length > MaxResponseBytes)
                {
                    return VoiceSynthesisResult.Fail("响应音频超过大小上限。");
                }

                return VoiceSynthesisResult.Ok(bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                return VoiceSynthesisResult.Fail("无法连接 GPT-SoVITS：" + exception.Message);
            }
            catch (TaskCanceledException)
            {
                return VoiceSynthesisResult.Fail("语音合成超时。");
            }
        }

        /// <summary>同进程权重去重：配置未变跳过 set_*_weights；TTS 失败后允许重新下发一次。</summary>
        private async Task<string?> SwitchModelsIfConfiguredAsync(
            string endpoint,
            VoiceConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(configuration.GptModelPath)
                && _lastAppliedGptPath == configuration.GptModelPath
                && !string.IsNullOrWhiteSpace(configuration.SovitsModelPath)
                && _lastAppliedSovitsPath == configuration.SovitsModelPath)
            {
                return null; // 已应用过相同权重
            }

            if (!string.IsNullOrWhiteSpace(configuration.GptModelPath))
            {
                var url = endpoint + "/set_gpt_weights?weights_path="
                    + Uri.EscapeDataString(configuration.GptModelPath);
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return "切换 GPT 权重失败：HTTP " + (int)response.StatusCode;
                }
            }

            if (!string.IsNullOrWhiteSpace(configuration.SovitsModelPath))
            {
                var url = endpoint + "/set_sovits_weights?weights_path="
                    + Uri.EscapeDataString(configuration.SovitsModelPath);
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return "切换 SoVITS 权重失败：HTTP " + (int)response.StatusCode;
                }
            }

            _lastAppliedGptPath = configuration.GptModelPath;
            _lastAppliedSovitsPath = configuration.SovitsModelPath;
            return null;
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}





