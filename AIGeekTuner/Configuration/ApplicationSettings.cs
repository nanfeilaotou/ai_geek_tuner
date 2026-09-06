namespace AIGeekTuner.Configuration
{
    /// <summary>
    /// 持久化的用户设置。属性全部 init-only：磁盘加载与保存替换都以整体快照方式进行，
    /// 运行中的诊断只持有旧快照，不会被中途修改影响。
    /// </summary>
    public sealed class ApplicationSettings
    {
        public bool AutoSaveDiagnosisHistory { get; init; } = true;

        public string OllamaBaseUrl { get; init; } = OllamaOptions.DefaultBaseUrl;

        public string OllamaModelName { get; init; } = OllamaOptions.DefaultModelName;

        /// <summary>
        /// Global AI request timeout. The property name is retained in the
        /// runtime persistence schema for migration; portable v2 calls it
        /// aiTimeoutSeconds.
        /// </summary>
        public int OllamaTimeoutSeconds { get; init; } = OllamaOptions.DefaultTimeoutSeconds;

        public int MaxFaultLogCharacters { get; init; } =
            DiagnosisInputOptions.DefaultMaxFaultLogCharacters;

        public bool UseJsonFormat { get; init; } = OllamaOptions.DefaultUseJsonFormat;

        /// <summary>诊断录制采样间隔（毫秒）。合法值由 ApplicationSettingsValidator 定义。</summary>
        public int RecordingIntervalMs { get; init; } = 2000;

        public VoiceSettings Voice { get; init; } = new();

        /// <summary>Hardware 页自动刷新开关与间隔（UI 偏好，非录制采样）。</summary>
        public bool HardwareAutoRefresh { get; init; } = true;

        public int HardwareRefreshIntervalMs { get; init; } = 2000;
    }

    /// <summary>GPT-SoVITS 语音摘要配置。ReferenceAudioPath 必须是 GSV 服务进程可访问的路径。</summary>
    public sealed class VoiceSettings
    {
        public bool Enabled { get; init; }

        public string Endpoint { get; init; } = "http://127.0.0.1:9880";

        public string ReferenceAudioPath { get; init; }
            = "D:\\AI\\GPT-SoVITS\\wav\\idle50.wav";

        /// <summary>参考音频的转写文本；本默认参考音频为日语。</summary>
        public string PromptText { get; init; }
            = "\u3069\u3046\u3057\u307e\u3057\u305f\u79c1\u304c\u3042\u307e\u308a\u53ef\u611b\u3044\u304b\u3089\u3073\u3063\u304f\u308a\u3057\u3061\u3083\u3063\u305f\u3093\u3067\u3059\u304b";

        /// <summary>参考音频语言；合成正文语言固定 zh（§38）。</summary>
        public string PromptLang { get; init; } = "ja";

        public double SpeedFactor { get; init; } = 1.0;

        /// <summary>可选：按官方 /set_gpt_weights 下发的权重路径（用户显式配置才生效）。</summary>
        public string GptModelPath { get; init; }
            = "D:\\AI\\GPT-SoVITS\\GPT_weights_v2Pro\\\u5c0f\u9152\u72d0-e15.ckpt";

        public string SovitsModelPath { get; init; }
            = "D:\\AI\\GPT-SoVITS\\SoVITS_weights_v2Pro\\\u5c0f\u9152\u72d0_e8_s184.pth";
    }
}
