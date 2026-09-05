using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;

namespace AIGeekTuner.Tests.TestSupport;

/// <summary>内存版 IAiProviderProfileStore：快照可编程替换，保存即整体换入（不落盘）。</summary>
public sealed class ScriptedProviderStore : IAiProviderProfileStore
{
    private AiProviderConfiguration _current;

    public ScriptedProviderStore(AiProviderConfiguration? initial = null)
    {
        _current = initial ?? AiProviderConfiguration.Empty;
    }

    public List<AiProviderConfiguration> SavedConfigurations { get; } = [];

    public AiProviderConfiguration Snapshot() => _current;

    public Task SaveAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        SavedConfigurations.Add(configuration);
        _current = configuration;
        return Task.CompletedTask;
    }

    public void Replace(AiProviderConfiguration configuration) => _current = configuration;
}

/// <summary>内存版 IAiCredentialStore：明文只在字典里，供断言“读取次数 / 是否读取”。</summary>
public sealed class FakeAiCredentialStore : IAiCredentialStore
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public List<string> LoadCalls { get; } = [];

    public int LoadCallCount => LoadCalls.Count;

    public Task SaveAsync(string providerId, string plainTextSecret, CancellationToken cancellationToken = default)
    {
        Secrets[providerId] = plainTextSecret;
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        LoadCalls.Add(providerId);
        return Task.FromResult(Secrets.TryGetValue(providerId, out var secret) ? secret : null);
    }

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        Secrets.Remove(providerId);
        return Task.CompletedTask;
    }
}
