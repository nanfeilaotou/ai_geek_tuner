namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>保存 Profile 时对凭据的处置方式。明文只通过 Replace 传入，绝不持久化在配置对象上。</summary>
    public enum AiCredentialChangeMode
    {
        /// <summary>不改动已存凭据（新建 Provider 时等价于“暂无凭据”）。</summary>
        KeepExisting,

        /// <summary>用 PlainText 替换凭据（DPAPI 加密后落盘）。</summary>
        Replace,

        /// <summary>删除该 Provider 的凭据。</summary>
        Delete
    }

    public sealed record AiCredentialChange(
        AiCredentialChangeMode Mode,
        string? PlainText = null)
    {
        public static AiCredentialChange Keep { get; } = new(AiCredentialChangeMode.KeepExisting);
    }
}
