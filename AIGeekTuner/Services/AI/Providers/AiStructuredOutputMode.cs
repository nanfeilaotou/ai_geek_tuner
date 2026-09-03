namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// 结构化输出（约束模型输出为合法 JSON/Schema）的支持方式。
    /// 不同 Provider / 端点能力差异很大，必须由用户或预设显式声明，
    /// 绝不自动假定所有 OpenAI 兼容端点都支持 json_schema。
    /// </summary>
    public enum AiStructuredOutputMode
    {
        /// <summary>Ollama 原生 /api/chat 的顶层 format 字段（JSON Schema）。</summary>
        NativeSchema,

        /// <summary>OpenAI 兼容的 response_format: json_schema。</summary>
        OpenAiJsonSchema,

        /// <summary>OpenAI 兼容的 response_format: json_object（仅约束为合法 JSON）。</summary>
        JsonObject,

        /// <summary>不做协议层约束，只靠提示词要求 JSON。</summary>
        PromptOnly
    }
}
