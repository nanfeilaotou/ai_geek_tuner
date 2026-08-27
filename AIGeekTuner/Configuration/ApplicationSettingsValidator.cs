namespace AIGeekTuner.Configuration
{
    /// <summary>
    /// 设置保存前的唯一权威校验入口；取值范围集中在此定义，
    /// UI 与持久化服务都不得各自硬编码范围。
    /// </summary>
    public static class ApplicationSettingsValidator
    {
        public const int MinTimeoutSeconds = 5;

        public const int MaxTimeoutSeconds = 3600;

        // 下界与 DiagnosisPromptBuilder 的最低要求保持一致。
        public const int MinFaultLogCharacters = 1_000;

        public const int MaxFaultLogCharactersLimit = 200_000;

        /// <summary>录制采样间隔只允许 1/2/5 秒（§3：不做更高频率）。</summary>
        public static readonly IReadOnlyList<int> AllowedRecordingIntervalsMs = [1000, 2000, 5000];

        public static IReadOnlyList<string> Validate(ApplicationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var errors = new List<string>();

            if (!Uri.TryCreate(settings.OllamaBaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("服务地址必须是有效的 http:// 或 https:// 地址。");
            }

            if (string.IsNullOrWhiteSpace(settings.OllamaModelName))
            {
                errors.Add("模型名称不能为空。");
            }

            if (settings.OllamaTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            {
                errors.Add($"请求超时必须在 {MinTimeoutSeconds} 到 {MaxTimeoutSeconds} 秒之间。");
            }

            if (settings.MaxFaultLogCharacters is < MinFaultLogCharacters
                    or > MaxFaultLogCharactersLimit)
            {
                errors.Add(
                    $"诊断输入长度必须在 {MinFaultLogCharacters:N0} 到 {MaxFaultLogCharactersLimit:N0} 字符之间。");
            }

            if (!AllowedRecordingIntervalsMs.Contains(settings.HardwareRefreshIntervalMs))
            {
                errors.Add("硬件页刷新间隔只允许 1000 / 2000 / 5000 毫秒。");
            }            if (!AllowedRecordingIntervalsMs.Contains(settings.RecordingIntervalMs))
            {
                errors.Add("诊断录制采样间隔只允许 1000 / 2000 / 5000 毫秒。");
            }

            if (settings.Voice.Enabled)
            {
                if (!Uri.TryCreate(settings.Voice.Endpoint, UriKind.Absolute, out var voiceUri)
                    || (voiceUri.Scheme != Uri.UriSchemeHttp && voiceUri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add("语音服务地址必须是有效的 http:// 或 https:// 地址。");
                }

                if (settings.Voice.SpeedFactor is < 0.7 or > 1.3)
                {
                    errors.Add("语音速度必须在 0.7 到 1.3 之间。");
                }

                if (!AllowedPromptLanguages.Contains(settings.Voice.PromptLang,
                        StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add("参考音频语言仅支持 zh / ja / en。");
                }

                if (string.IsNullOrWhiteSpace(settings.Voice.ReferenceAudioPath))
                {
                    errors.Add("启用语音时必须填写参考音频路径。");
                }
            }

            return errors;
        }

        public static readonly IReadOnlyList<string> AllowedPromptLanguages = ["zh", "ja", "en"];
    }
}

