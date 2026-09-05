namespace AIGeekTuner.Services.AI.Providers.Configuration
{
    /// <summary>
    /// ai-providers.json 的文档结构：Provider 列表 + 文档版本 + 当前使用（Active）Provider。
    /// 只存非敏感配置；API Key 在独立的 DPAPI 凭据文件里。
    /// ActiveProviderId（M5.1B，additive）：稳定 Provider ID，决定下一次 AI 请求用谁。
    /// 旧文件缺失该字段时反序列化为 null，运行期由 fallback 策略解析（Gate C）。
    /// </summary>
    public sealed class AiProviderConfiguration
    {
        public int Version { get; init; } = 1;

        public IReadOnlyList<AiProviderProfile> Profiles { get; init; } = Array.Empty<AiProviderProfile>();

        /// <summary>“当前使用”的 Provider 稳定 ID；null 表示从未显式设置（走 fallback 策略）。</summary>
        public string? ActiveProviderId { get; init; }

        public static AiProviderConfiguration Empty { get; } = new();
    }
}
