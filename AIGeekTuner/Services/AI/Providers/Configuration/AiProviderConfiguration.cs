namespace AIGeekTuner.Services.AI.Providers.Configuration
{
    /// <summary>
    /// ai-providers.json 的文档结构：Provider 列表 + 文档版本。
    /// 只存非敏感配置；API Key 在独立的 DPAPI 凭据文件里。
    /// </summary>
    public sealed class AiProviderConfiguration
    {
        public int Version { get; init; } = 1;

        public IReadOnlyList<AiProviderProfile> Profiles { get; init; } = Array.Empty<AiProviderProfile>();

        public static AiProviderConfiguration Empty { get; } = new();
    }
}
