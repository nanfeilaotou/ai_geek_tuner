using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers.Runtime
{
    /// <summary>
    /// Provider Kind → 具体 transport 的固定分派（Gate F）。
    /// 只有两个 Kind 一个 switch——DeepSeek / LM Studio / llama.cpp 等全部是
    /// OpenAI 兼容预设，不需要也不允许出现按厂商命名的 transport。
    /// </summary>
    public sealed class AiChatTransportDispatcher : IAiChatTransport
    {
        private readonly OllamaNativeChatTransport _ollamaNative;
        private readonly OpenAiCompatibleChatTransport _openAiCompatible;

        public AiChatTransportDispatcher(
            OllamaNativeChatTransport ollamaNative,
            OpenAiCompatibleChatTransport openAiCompatible)
        {
            _ollamaNative = ollamaNative ?? throw new ArgumentNullException(nameof(ollamaNative));
            _openAiCompatible = openAiCompatible ?? throw new ArgumentNullException(nameof(openAiCompatible));
        }

        public Task<AiChatResponse> SendAsync(
            AiChatRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return request.Runtime.ProviderKind switch
            {
                AiProviderKind.OllamaNative => _ollamaNative.SendAsync(request, cancellationToken),
                AiProviderKind.OpenAiCompatible => _openAiCompatible.SendAsync(request, cancellationToken),
                _ => throw new AiRuntimeException(
                    AiRuntimeError.ProtocolError,
                    "未知的 Provider 协议类型，无法发起 AI 请求。")
            };
        }
    }

    /// <summary>Active Provider 解析与 fallback 策略（Gate B/C）的唯一实现。</summary>
    public static class AiActiveProviderResolver
    {
        /// <summary>
        /// 解析“当前使用”的 Provider：
        /// 1. 已保存的合法 ActiveProviderId（指向已保存、Enabled、有默认模型的 profile）；
        /// 2. 没有 / 失效时：优先由旧 Ollama 设置迁移出的 ollama profile（升级用户不突然失去 AI）；
        /// 3. 否则第一个合法 Enabled profile；
        /// 4. 一个都没有 → null（调用方统一映射为“尚未配置可用的 AI 服务提供方”）。
        /// 绝不静默创建假配置。
        /// </summary>
        public static AiProviderProfile? Resolve(AiProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var candidates = configuration.Profiles
                .Where(IsUsable)
                .ToArray();

            if (!string.IsNullOrWhiteSpace(configuration.ActiveProviderId))
            {
                var active = candidates.FirstOrDefault(profile =>
                    string.Equals(profile.Id, configuration.ActiveProviderId, StringComparison.Ordinal));
                if (active is not null)
                {
                    return active;
                }
            }

            var legacyMigrated = candidates.FirstOrDefault(profile =>
                string.Equals(profile.Id, AiProviderPresets.OllamaPresetId, StringComparison.Ordinal));
            if (legacyMigrated is not null)
            {
                return legacyMigrated;
            }

            return candidates.FirstOrDefault();
        }

        /// <summary>“合法可用”= Enabled 且默认模型已设置（runtime selection 需要 DefaultModelId）。</summary>
        public static bool IsUsable(AiProviderProfile profile) =>
            profile.Enabled && !string.IsNullOrWhiteSpace(profile.DefaultModelId);
    }

    /// <summary>
    /// 运行时快照捕获源（Gate D/E）：Active profile（已保存版本）+ 捕获时读取一次的凭据
    /// + 调用方传入的全局超时。捕获之后 Store / Settings / 凭据的任何变化都不影响本次请求。
    /// </summary>
    public interface IAiRuntimeSnapshotSource
    {
        /// <summary>无可用 Provider（含 fallback 失败）时返回 null，绝不创建假配置。</summary>
        Task<AiRuntimeSnapshot?> TryCaptureAsync(
            int timeoutSeconds,
            CancellationToken cancellationToken = default);
    }

    public sealed class AiRuntimeSnapshotSource : IAiRuntimeSnapshotSource
    {
        private readonly IAiProviderProfileStore _store;
        private readonly IAiCredentialStore _credentials;

        public AiRuntimeSnapshotSource(
            IAiProviderProfileStore store,
            IAiCredentialStore credentials)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        }

        public async Task<AiRuntimeSnapshot?> TryCaptureAsync(
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            var profile = AiActiveProviderResolver.Resolve(_store.Snapshot());
            if (profile is null)
            {
                return null;
            }

            var modelId = AiProviderModelId.Normalize(profile.DefaultModelId);
            if (modelId is null)
            {
                return null;
            }

            // 凭据只在捕获时刻读取一次，明文只存在于快照（= 当前请求生命周期）。
            // Ollama Native 没有鉴权概念，绝不读取凭据。
            string? apiKey = null;
            if (profile.Kind == AiProviderKind.OpenAiCompatible)
            {
                apiKey = await _credentials.LoadAsync(profile.Id, cancellationToken);
            }

            return new AiRuntimeSnapshot(
                profile.Id,
                profile.DisplayName,
                profile.Kind,
                profile.BaseUrl,
                modelId,
                profile.StructuredOutputMode,
                apiKey,
                timeoutSeconds);
        }
    }
}
