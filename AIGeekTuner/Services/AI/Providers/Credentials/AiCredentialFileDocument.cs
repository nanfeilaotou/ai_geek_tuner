namespace AIGeekTuner.Services.AI.Providers.Credentials
{
    /// <summary>
    /// credentials.json 的文档结构。只允许两种字段：
    /// providerId 与 protectedBlobBase64——绝不存明文。
    /// </summary>
    public sealed class AiCredentialFileDocument
    {
        public int Version { get; set; } = 1;

        public List<AiCredentialFileEntry> Credentials { get; set; } = new();
    }

    public sealed class AiCredentialFileEntry
    {
        public string ProviderId { get; set; } = string.Empty;

        public string ProtectedBlobBase64 { get; set; } = string.Empty;
    }
}
