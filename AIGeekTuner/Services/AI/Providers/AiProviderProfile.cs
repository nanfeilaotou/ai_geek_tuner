namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// 一个 AI Provider 的完整配置（不可变对象，修改即整体替换）。
    /// Id 是稳定身份；DisplayName / BaseUrl / 模型列表都可随时修改。
    /// API Key 永远不落在本对象上——凭据由 <see cref="IAiCredentialStore"/> 按 Id 单独保管。
    /// </summary>
    public sealed class AiProviderProfile
    {
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public AiProviderKind Kind { get; init; }

        /// <summary>API 根地址，例如 http://localhost:11434 或 http://127.0.0.1:1234/v1。</summary>
        public string BaseUrl { get; init; } = string.Empty;

        public IReadOnlyList<AiProviderModel> Models { get; init; } = Array.Empty<AiProviderModel>();

        /// <summary>默认模型；为空表示尚未选择。必须是 <see cref="Models"/> 中的某个 Id。</summary>
        public string? DefaultModelId { get; init; }

        public AiStructuredOutputMode StructuredOutputMode { get; init; }

        public bool Enabled { get; init; } = true;
    }
}
