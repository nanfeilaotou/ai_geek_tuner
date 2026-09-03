namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// Provider 下的一个模型条目。第一版只有 Id 与可选显示名，
    /// 不携带 pricing / 上下文长度 / 视觉与工具元数据——
    /// 除非端点能可靠提供，否则不猜。
    /// </summary>
    public sealed class AiProviderModel
    {
        public AiProviderModel()
        {
        }

        public AiProviderModel(string id, string? displayName = null)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; init; } = string.Empty;

        public string? DisplayName { get; init; }
    }
}
