using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers.Runtime
{
    /// <summary>
    /// Provider 中立的运行时错误分类（Gate P）。
    /// 用户可见文案一律来自 <see cref="AiRuntimeException.UserMessage"/>（已脱敏），
    /// 禁止把 HttpRequestException / 原始响应体 / 堆栈直接抛给 UI。
    /// </summary>
    public enum AiRuntimeError
    {
        /// <summary>没有任何可用的已保存 Provider（含 fallback 失败）。</summary>
        NotConfigured,

        ConnectionUnavailable,

        AuthenticationFailed,

        ModelRejected,

        Timeout,

        Cancelled,

        InvalidResponse,

        /// <summary>协议层不匹配（例如 OpenAI 兼容端点被配置为 NativeSchema）。</summary>
        ProtocolError
    }

    /// <summary>Provider 中立的运行时异常；Message 即用户可见中文文案（已脱敏）。</summary>
    public sealed class AiRuntimeException : Exception
    {
        public AiRuntimeException(AiRuntimeError error, string userMessage)
            : base(userMessage)
        {
            Error = error;
            UserMessage = userMessage;
        }

        public AiRuntimeException(AiRuntimeError error, string userMessage, Exception innerException)
            : base(userMessage, innerException)
        {
            Error = error;
            UserMessage = userMessage;
        }

        public AiRuntimeError Error { get; }

        public string UserMessage { get; }
    }

    /// <summary>
    /// 一次 AI 请求生命周期的不可变运行时快照（Gate D）。
    /// 开始请求前捕获一次：之后 Provider / 模型 / 设置如何变化都与本次请求无关。
    /// <see cref="ApiKey"/> 严格 ephemeral：只在捕获时读取一次，只存在于当前请求生命周期，
    /// 绝不进入日志 / 持久化 / ToString。
    /// </summary>
    public sealed record AiRuntimeSnapshot(
        string ProviderId,
        string ProviderDisplayName,
        AiProviderKind ProviderKind,
        string BaseUrl,
        string ModelId,
        AiStructuredOutputMode StructuredOutputMode,
        string? ApiKey,
        int TimeoutSeconds)
    {
        public override string ToString() =>
            ProviderId + "（" + ProviderKind + "），模型 " + ModelId
            + "，超时 " + TimeoutSeconds + "s，"
            + (ApiKey is null ? "无凭据" : "凭据已配置");
    }

    public enum AiStructuredOutputIntent
    {
        /// <summary>本次调用不需要任何协议层 JSON 约束。</summary>
        None,

        /// <summary>调用方只需要“输出是合法 JSON 对象”（没有完整 schema）。</summary>
        JsonObject,

        /// <summary>调用方提供完整 JSON Schema。</summary>
        JsonSchema
    }

    /// <summary>
    /// Provider 中立的结构化输出请求（Gate H）：由调用方声明自己需要什么
    /// （none / JsonObject / JsonSchema），协议层具体怎么发由 transport 按
    /// Provider Kind 与 Profile 的 <see cref="AiStructuredOutputMode"/> 映射。
    /// </summary>
    public sealed record AiStructuredOutputRequest(
        AiStructuredOutputIntent Intent,
        string? JsonSchema = null)
    {
        public static AiStructuredOutputRequest None { get; } = new(AiStructuredOutputIntent.None);

        public static AiStructuredOutputRequest JsonObject { get; } = new(AiStructuredOutputIntent.JsonObject);

        public static AiStructuredOutputRequest WithSchema(string jsonSchema) =>
            new(AiStructuredOutputIntent.JsonSchema, jsonSchema);
    }

    public sealed record AiChatMessage(string Role, string Content);

    /// <summary>本次调用的采样/生成选项。null 表示“沿用服务端默认”，与现有各链路行为逐字段等价。</summary>
    public sealed record AiChatRuntimeOptions(
        bool? Think = null,
        double? Temperature = null,
        int? MaxOutputTokens = null);

    public sealed record AiChatRequest(
        AiRuntimeSnapshot Runtime,
        IReadOnlyList<AiChatMessage> Messages,
        AiStructuredOutputRequest StructuredOutput,
        AiChatRuntimeOptions Options);

    public sealed record AiChatResponse(string Content, string? Model = null);

    /// <summary>
    /// 小而明确的 provider-aware chat transport seam（Gate F）。
    /// 只认识 chat/messages/JSON 约束——绝不认识 DiagnosticResult / SessionAnalysisResult；
    /// 业务语义（Prompt、Parser、repair、SafetyGuard）全部留在调用方。
    /// </summary>
    public interface IAiChatTransport
    {
        Task<AiChatResponse> SendAsync(AiChatRequest request, CancellationToken cancellationToken = default);
    }
}
