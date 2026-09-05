using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// Provider foundation 的小型管理入口。职责只有：
    /// 列出 / 读取 profile、校验草稿、保存 / 删除、用“当前草稿”发现模型与测试连接。
    /// 不做 Plugin Registry / Factory 层级 / Service Locator——两种 Kind 一个 switch 足够。
    /// </summary>
    public interface IAiProviderManager
    {
        IReadOnlyList<AiProviderProfile> ListProfiles();

        AiProviderProfile? GetProfile(string providerId);

        /// <summary>校验草稿（含与其他 profile 的 Id 冲突检查）；返回空列表表示通过。</summary>
        IReadOnlyList<string> ValidateDraft(AiProviderProfile draft);

        /// <summary>
        /// 用“当前草稿”发现模型（允许未保存的 BaseUrl / 凭据）。
        /// 绝不保存任何东西。plainApiKey 非空时优先使用草稿里的明文 Key；
        /// 否则回退到该 Provider Id 已保存的凭据。
        /// </summary>
        Task<AiModelDiscoveryResult> FetchModelsAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 用“当前草稿”测试连接：优先对已选模型做最小 chat/completions 探测。
        /// 绝不保存任何东西。
        /// </summary>
        Task<AiConnectionTestResult> TestConnectionAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            string? modelId = null,
            CancellationToken cancellationToken = default);

        /// <summary>结构化输出能力探测；失败只是“不支持”，绝不使 Provider 失效，也不改用户配置。</summary>
        Task<AiStructuredOutputProbeResult> TestStructuredOutputAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            string? modelId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// “保存并应用”：校验 → 写凭据 → 原子写配置 → 换内存快照。
        /// 配置写失败时回滚凭据，不产生 half-applied 状态。
        /// </summary>
        Task<AiProviderSaveResult> SaveProfileAsync(
            AiProviderProfile draft,
            AiCredentialChange credentialChange,
            CancellationToken cancellationToken = default);

        /// <summary>删除 Provider 及其凭据；凭据删除失败时中止且不删配置。</summary>
        Task<AiProviderSaveResult> DeleteProfileAsync(
            string providerId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 解析“当前使用”的 Provider（M5.1B，Gate B/C）：“当前编辑”只影响设置草稿，
        /// 这里返回的才是下一次 AI 请求实际使用的已保存 profile。
        /// 策略：显式 ActiveProviderId → 迁移出的 ollama profile → 第一个合法 Enabled profile → null。
        /// </summary>
        AiProviderProfile? ResolveActiveProvider();

        /// <summary>
        /// 把一个已经保存的 profile 设为“当前使用”：原子持久化 ActiveProviderId。
        /// 禁止对未保存草稿 / 未启用 / 缺默认模型的 profile 生效；下次请求立即生效，无需重启。
        /// </summary>
        Task<AiProviderSaveResult> SetActiveProviderAsync(
            string providerId,
            CancellationToken cancellationToken = default);
    }

    public sealed record AiProviderSaveResult(bool Success, string? Error = null)
    {
        public static AiProviderSaveResult Ok() => new(true);

        public static AiProviderSaveResult Fail(string error) => new(false, error);
    }
}
