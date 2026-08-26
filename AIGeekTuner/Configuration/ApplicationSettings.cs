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

        public int OllamaTimeoutSeconds { get; init; } = OllamaOptions.DefaultTimeoutSeconds;

        public int MaxFaultLogCharacters { get; init; } =
            DiagnosisInputOptions.DefaultMaxFaultLogCharacters;

        public bool UseJsonFormat { get; init; } = OllamaOptions.DefaultUseJsonFormat;
    }
}
