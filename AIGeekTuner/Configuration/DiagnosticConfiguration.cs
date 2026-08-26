namespace AIGeekTuner.Configuration
{
    /// <summary>
    /// 一次诊断操作全程使用的不可变配置快照：
    /// 开始操作时从存储取出，之后即使设置被保存为新版本也不影响本次操作。
    /// </summary>
    public sealed record DiagnosticConfiguration(
        OllamaOptions Ollama,
        DiagnosisInputOptions Input);

    /// <summary>
    /// 线程安全的当前配置持有者。Replace 以原子方式整体换入新快照，
    /// Snapshot 始终返回某个完整版本，绝不出现“读到半新半旧配置”的状态。
    /// </summary>
    public sealed class DiagnosticConfigurationStore
    {
        private DiagnosticConfiguration _current;

        public DiagnosticConfigurationStore(DiagnosticConfiguration initial)
        {
            _current = initial ?? throw new ArgumentNullException(nameof(initial));
        }

        public DiagnosticConfiguration Snapshot() => Volatile.Read(ref _current);

        public void Replace(DiagnosticConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            Volatile.Write(ref _current, configuration);
        }
    }
}
