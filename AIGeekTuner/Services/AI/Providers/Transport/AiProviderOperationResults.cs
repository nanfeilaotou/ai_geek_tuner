namespace AIGeekTuner.Services.AI.Providers.Transport
{
    /// <summary>
    /// /models 发现的四态结果。关键设计（来自 Open WebUI 的成熟经验）：
    /// /models 不可用 ≠ Provider 不可用——很多 OpenAI 兼容服务
    /// /models 不存在或路径特殊，但 chat/completions 可用。
    /// 因此 ModelsUnavailable 只是“模型发现失败”，UI/Store 永远允许手动添加模型。
    /// </summary>
    public enum AiModelDiscoveryStatus
    {
        ModelsDiscovered,
        ModelsUnavailable,
        ConnectionUnavailable,
        AuthenticationFailed
    }

    public sealed record AiModelDiscoveryResult(
        AiModelDiscoveryStatus Status,
        string Message,
        IReadOnlyList<string> Models);

    /// <summary>Test Connection 的结果状态。MissingModel 表示草稿里既没传模型也没有默认模型。</summary>
    public enum AiConnectionTestStatus
    {
        Connected,
        AuthenticationFailed,
        ModelRejected,
        ConnectionUnavailable,
        InvalidResponse,
        MissingModel
    }

    public sealed record AiConnectionTestResult(
        AiConnectionTestStatus Status,
        string Message);

    /// <summary>结构化输出能力探测结果：只是建议，失败绝不让 Provider 失效，也不自动改配置。</summary>
    public sealed record AiStructuredOutputProbeResult(
        bool Supported,
        string Message);
}
