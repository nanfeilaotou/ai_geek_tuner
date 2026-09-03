namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// 第一版内置预设。预设只负责创建初始草稿——
    /// 用户之后可以随意修改 BaseUrl / 模型等任何字段。
    /// LM Studio 等只是预设（OpenAI 兼容），不是独立 Provider Kind。
    /// </summary>
    public static class AiProviderPresets
    {
        public const string OllamaPresetId = "ollama";

        public const string LmStudioPresetId = "lmstudio";

        public const string OllamaDefaultBaseUrl = "http://localhost:11434";

        public const string LmStudioDefaultBaseUrl = "http://127.0.0.1:1234/v1";

        public static AiProviderProfile CreateOllamaDraft()
        {
            return new AiProviderProfile
            {
                Id = OllamaPresetId,
                DisplayName = "Ollama（本机）",
                Kind = AiProviderKind.OllamaNative,
                BaseUrl = OllamaDefaultBaseUrl,
                Models = Array.Empty<AiProviderModel>(),
                StructuredOutputMode = AiStructuredOutputMode.NativeSchema,
                Enabled = true
            };
        }

        public static AiProviderProfile CreateLmStudioDraft()
        {
            return new AiProviderProfile
            {
                Id = LmStudioPresetId,
                DisplayName = "LM Studio（本机）",
                Kind = AiProviderKind.OpenAiCompatible,
                BaseUrl = LmStudioDefaultBaseUrl,
                Models = Array.Empty<AiProviderModel>(),
                // LM Studio 支持 response_format json_schema；是否真正可用仍以端点实测为准。
                StructuredOutputMode = AiStructuredOutputMode.OpenAiJsonSchema,
                Enabled = true
            };
        }

        /// <summary>自定义 OpenAI 兼容端点的空白草稿；结构化输出默认 PromptOnly（最保守）。</summary>
        public static AiProviderProfile CreateCustomOpenAiCompatibleDraft(
            string id,
            string displayName,
            string baseUrl)
        {
            return new AiProviderProfile
            {
                Id = id,
                DisplayName = displayName,
                Kind = AiProviderKind.OpenAiCompatible,
                BaseUrl = baseUrl,
                Models = Array.Empty<AiProviderModel>(),
                StructuredOutputMode = AiStructuredOutputMode.PromptOnly,
                Enabled = true
            };
        }
    }
}
