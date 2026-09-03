namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>手动模型条目的规范：trim、去重由校验器负责，这里只定长度上限。</summary>
    public static class AiProviderModelId
    {
        public const int MaxLength = 200;

        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();
            return trimmed.Length > MaxLength ? null : trimmed;
        }
    }
}
