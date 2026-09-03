using System.IO;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// <see cref="IAiProviderManager"/> 的默认实现。
    /// 草稿语义（Gate M）：FetchModels / TestConnection 只使用调用方传入的草稿与明文 Key，
    /// 永不写盘；只有 SaveProfileAsync / DeleteProfileAsync 触碰持久化。
    /// 保存原子性（Gate N）：凭据先写、配置后写；配置失败回滚凭据，
    /// 内存快照由 store 在磁盘写成功后整体换入，绝不出现 half-applied。
    /// </summary>
    public sealed class AiProviderManager : IAiProviderManager
    {
        private readonly IAiProviderProfileStore _store;
        private readonly IAiCredentialStore _credentials;
        private readonly IOpenAiCompatibleClient _openAiClient;
        private readonly IOllamaNativeClient _ollamaClient;

        public AiProviderManager(
            IAiProviderProfileStore store,
            IAiCredentialStore credentials,
            IOpenAiCompatibleClient openAiClient,
            IOllamaNativeClient ollamaClient)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _openAiClient = openAiClient ?? throw new ArgumentNullException(nameof(openAiClient));
            _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        }

        public IReadOnlyList<AiProviderProfile> ListProfiles()
        {
            return _store.Snapshot().Profiles;
        }

        public AiProviderProfile? GetProfile(string providerId)
        {
            return _store.Snapshot().Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, providerId, StringComparison.Ordinal));
        }

        public IReadOnlyList<string> ValidateDraft(AiProviderProfile draft)
        {
            var errors = new List<string>(AiProviderProfileValidator.Validate(draft));
            if (errors.Count == 0)
            {
                var conflict = _store.Snapshot().Profiles.FirstOrDefault(profile =>
                    !string.Equals(profile.Id, draft.Id, StringComparison.Ordinal)
                    && string.Equals(profile.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
                if (conflict is not null)
                {
                    errors.Add($"Provider ID {draft.Id} 已被其他 Provider 使用（ID 创建后不可修改）。");
                }
            }

            return errors;
        }

        public Task<AiModelDiscoveryResult> FetchModelsAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            return draft.Kind switch
            {
                AiProviderKind.OllamaNative => _ollamaClient.ListModelsAsync(draft.BaseUrl, cancellationToken),
                AiProviderKind.OpenAiCompatible => ResolveKeyAndRunAsync(
                    draft, plainApiKey,
                    key => _openAiClient.ListModelsAsync(draft.BaseUrl, key, cancellationToken)),
                _ => Task.FromResult(new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "未知的 Provider 协议类型。",
                    Array.Empty<string>()))
            };
        }

        public Task<AiConnectionTestResult> TestConnectionAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var model = AiProviderModelId.Normalize(modelId)
                ?? AiProviderModelId.Normalize(draft.DefaultModelId);
            if (model is null)
            {
                return Task.FromResult(new AiConnectionTestResult(
                    AiConnectionTestStatus.MissingModel,
                    "未指定模型，无法测试连接。请先选择或手动添加模型。"));
            }

            return draft.Kind switch
            {
                AiProviderKind.OllamaNative => _ollamaClient.ProbeChatAsync(draft.BaseUrl, model, cancellationToken),
                AiProviderKind.OpenAiCompatible => ResolveKeyAndRunAsync(
                    draft, plainApiKey,
                    key => _openAiClient.ProbeChatAsync(draft.BaseUrl, key, model, cancellationToken)),
                _ => Task.FromResult(new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "未知的 Provider 协议类型。"))
            };
        }

        public Task<AiStructuredOutputProbeResult> TestStructuredOutputAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var model = AiProviderModelId.Normalize(modelId)
                ?? AiProviderModelId.Normalize(draft.DefaultModelId);
            if (model is null)
            {
                return Task.FromResult(new AiStructuredOutputProbeResult(
                    false,
                    "未指定模型，无法探测结构化输出能力。"));
            }

            return draft.Kind switch
            {
                AiProviderKind.OllamaNative => _ollamaClient.ProbeStructuredOutputAsync(draft.BaseUrl, model, cancellationToken),
                AiProviderKind.OpenAiCompatible => ResolveKeyAndRunAsync(
                    draft, plainApiKey,
                    key => _openAiClient.ProbeStructuredOutputAsync(draft.BaseUrl, key, model, cancellationToken)),
                _ => Task.FromResult(new AiStructuredOutputProbeResult(
                    false,
                    "未知的 Provider 协议类型。"))
            };
        }

        public async Task<AiProviderSaveResult> SaveProfileAsync(
            AiProviderProfile draft,
            AiCredentialChange credentialChange,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(credentialChange);

            var errors = ValidateDraft(draft);
            if (errors.Count > 0)
            {
                return AiProviderSaveResult.Fail(string.Join(" ", errors));
            }

            if (credentialChange.Mode == AiCredentialChangeMode.Replace
                && string.IsNullOrEmpty(credentialChange.PlainText))
            {
                return AiProviderSaveResult.Fail("新凭据内容不能为空。");
            }

            var snapshot = _store.Snapshot();
            var profiles = snapshot.Profiles
                .Where(profile => !string.Equals(profile.Id, draft.Id, StringComparison.Ordinal))
                .Concat(new[] { draft })
                .ToArray();
            var configuration = new AiProviderConfiguration
            {
                Version = snapshot.Version,
                Profiles = profiles
            };

            try
            {
                // 1. 先写凭据（DPAPI 加密 + 原子落盘）。
                var rollbackPlainText = await ApplyCredentialChangeAsync(draft.Id, credentialChange, cancellationToken);

                // 2. 再原子写配置；失败则回滚凭据，避免 half-applied。
                try
                {
                    await _store.SaveAsync(configuration, cancellationToken);
                }
                catch (Exception)
                {
                    await RollbackCredentialAsync(draft.Id, rollbackPlainText);
                    throw;
                }

                return AiProviderSaveResult.Ok();
            }
            catch (Exception exception) when (exception is AiProviderStoreException or IOException)
            {
                return AiProviderSaveResult.Fail(exception.Message);
            }
        }

        public async Task<AiProviderSaveResult> DeleteProfileAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return AiProviderSaveResult.Fail("Provider ID 不能为空。");
            }

            var snapshot = _store.Snapshot();
            var remaining = snapshot.Profiles
                .Where(profile => !string.Equals(profile.Id, providerId, StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == snapshot.Profiles.Count)
            {
                return AiProviderSaveResult.Fail($"Provider {providerId} 不存在。");
            }

            try
            {
                // 1. 先删凭据；失败则整个删除操作中止，配置保持完整。
                var oldPlainText = await _credentials.LoadAsync(providerId, cancellationToken);
                await _credentials.DeleteAsync(providerId, cancellationToken);

                // 2. 再原子写“少了一个 Provider”的配置；失败则回滚凭据。
                try
                {
                    await _store.SaveAsync(
                        new AiProviderConfiguration { Version = snapshot.Version, Profiles = remaining },
                        cancellationToken);
                }
                catch (Exception)
                {
                    await RollbackCredentialAsync(providerId, oldPlainText);
                    throw;
                }

                return AiProviderSaveResult.Ok();
            }
            catch (Exception exception) when (exception is AiProviderStoreException or IOException)
            {
                return AiProviderSaveResult.Fail(exception.Message);
            }
        }

        /// <summary>执行凭据变更；返回变更前的明文（用于失败回滚），原来不存在时返回 null。</summary>
        private async Task<string?> ApplyCredentialChangeAsync(
            string providerId,
            AiCredentialChange change,
            CancellationToken cancellationToken)
        {
            switch (change.Mode)
            {
                case AiCredentialChangeMode.KeepExisting:
                    return null;
                case AiCredentialChangeMode.Replace:
                {
                    var oldPlainText = await _credentials.LoadAsync(providerId, cancellationToken);
                    await _credentials.SaveAsync(providerId, change.PlainText!, cancellationToken);
                    return oldPlainText;
                }
                case AiCredentialChangeMode.Delete:
                {
                    var oldPlainText = await _credentials.LoadAsync(providerId, cancellationToken);
                    await _credentials.DeleteAsync(providerId, cancellationToken);
                    return oldPlainText;
                }
                default:
                    throw new InvalidOperationException("未知的凭据变更类型。");
            }
        }

        /// <summary>失败回滚：恢复旧明文；原来没有凭据时删除残留。回滚自身失败只留痕，不掩盖原始错误。</summary>
        private async Task RollbackCredentialAsync(string providerId, string? oldPlainText)
        {
            try
            {
                if (string.IsNullOrEmpty(oldPlainText))
                {
                    await _credentials.DeleteAsync(providerId);
                }
                else
                {
                    await _credentials.SaveAsync(providerId, oldPlainText);
                }
            }
            catch (Exception rollbackFailure)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(
                    rollbackFailure,
                    "AI provider credential rollback");
            }
        }

        private async Task<T> ResolveKeyAndRunAsync<T>(
            AiProviderProfile draft,
            string? plainApiKey,
            Func<string?, Task<T>> run)
        {
            var key = !string.IsNullOrWhiteSpace(plainApiKey)
                ? plainApiKey
                : await _credentials.LoadAsync(draft.Id);
            return await run(key);
        }
    }
}
