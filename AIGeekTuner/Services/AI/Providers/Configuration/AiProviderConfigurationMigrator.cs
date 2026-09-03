using AIGeekTuner.Configuration;

namespace AIGeekTuner.Services.AI.Providers.Configuration
{
    /// <summary>
    /// 旧版 Ollama 设置（BaseUrl / ModelName）→ 等价 ollama profile 的迁移。
    /// 只创建内存快照，不写任何文件；旧 settings.json 永远不被修改。
    /// </summary>
    public static class AiProviderConfigurationMigrator
    {
        public static AiProviderConfiguration MigrateFromLegacy(OllamaOptions? legacyOllamaOptions)
        {
            if (legacyOllamaOptions is null)
            {
                return AiProviderConfiguration.Empty;
            }

            var profile = new AiProviderProfile
            {
                Id = AiProviderPresets.OllamaPresetId,
                DisplayName = "Ollama（从旧设置迁移）",
                Kind = AiProviderKind.OllamaNative,
                BaseUrl = string.IsNullOrWhiteSpace(legacyOllamaOptions.BaseUrl)
                    ? AiProviderPresets.OllamaDefaultBaseUrl
                    : legacyOllamaOptions.BaseUrl,
                Models = new[]
                {
                    new AiProviderModel(legacyOllamaOptions.ModelName)
                },
                DefaultModelId = legacyOllamaOptions.ModelName,
                StructuredOutputMode = AiStructuredOutputMode.NativeSchema,
                Enabled = true
            };

            return new AiProviderConfiguration
            {
                Version = 1,
                Profiles = new[] { profile }
            };
        }
    }
}
