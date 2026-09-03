using System.IO;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers;

/// <summary>Gate D/O：Provider 配置存储（迁移、快照、原子保存、损坏恢复、旧设置不受影响）。</summary>
public sealed class AiProviderProfileStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string ProvidersPath => _temp.Combine("ai-providers.json");

    private static OllamaOptions LegacyOllama() => new()
    {
        BaseUrl = "http://localhost:11434",
        ModelName = "qwen3:8b"
    };

    [Fact]
    public void Migrate_LegacyOllamaSettings_CreatesEquivalentOllamaProfile_WithoutWritingFiles()
    {
        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());

        var snapshot = store.Snapshot();
        var profile = Assert.Single(snapshot.Profiles);
        Assert.Equal("ollama", profile.Id);
        Assert.Equal(AiProviderKind.OllamaNative, profile.Kind);
        Assert.Equal("http://localhost:11434", profile.BaseUrl);
        Assert.Equal("qwen3:8b", Assert.Single(profile.Models).Id);
        Assert.Equal("qwen3:8b", profile.DefaultModelId);
        Assert.Equal(AiStructuredOutputMode.NativeSchema, profile.StructuredOutputMode);

        // 迁移是纯 additive：配置文件不存在时不写盘，旧 settings.json 永不被触碰。
        Assert.False(File.Exists(ProvidersPath));
    }

    [Fact]
    public async Task Save_Roundtrip_PreservesProfilesAndStructuredOutputMode()
    {
        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());
        var configuration = store.Snapshot();
        var lmStudio = AiProviderPresets.CreateLmStudioDraft();
        var updated = new AiProviderConfiguration
        {
            Version = configuration.Version,
            Profiles = configuration.Profiles.Concat(new[] { lmStudio }).ToArray()
        };

        await store.SaveAsync(updated);

        var reloaded = new AiProviderProfileStore(ProvidersPath);
        var persisted = reloaded.Snapshot();
        Assert.Equal(2, persisted.Profiles.Count);
        var persistedLmStudio = persisted.Profiles.Single(profile => profile.Id == "lmstudio");
        Assert.Equal(AiStructuredOutputMode.OpenAiJsonSchema, persistedLmStudio.StructuredOutputMode);
        Assert.Equal("http://127.0.0.1:1234/v1", persistedLmStudio.BaseUrl);
    }

    [Fact]
    public async Task Save_DuplicateProfileIds_Rejected_AndFileUnchanged()
    {
        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());
        await store.SaveAsync(store.Snapshot());
        var before = File.ReadAllText(ProvidersPath);

        var duplicated = new AiProviderConfiguration
        {
            Profiles = new[]
            {
                AiProviderPresets.CreateOllamaDraft(),
                AiProviderPresets.CreateOllamaDraft()
            }
        };

        await Assert.ThrowsAsync<AiProviderStoreException>(() => store.SaveAsync(duplicated));
        Assert.Equal(before, File.ReadAllText(ProvidersPath));
    }

    [Fact]
    public async Task Save_InvalidProfile_Rejected()
    {
        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());
        var bad = new AiProviderProfile
        {
            Id = "Bad_ID",
            DisplayName = "坏",
            Kind = AiProviderKind.OpenAiCompatible,
            BaseUrl = "not-a-url",
            StructuredOutputMode = AiStructuredOutputMode.PromptOnly
        };
        var configuration = new AiProviderConfiguration { Profiles = new[] { bad } };

        await Assert.ThrowsAsync<AiProviderStoreException>(() => store.SaveAsync(configuration));
        Assert.False(File.Exists(ProvidersPath));
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndFallsBackToMigratedSnapshot()
    {
        File.WriteAllText(ProvidersPath, "{ this is not json");

        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());

        var profile = Assert.Single(store.Snapshot().Profiles);
        Assert.Equal("ollama", profile.Id);
        Assert.Empty(Directory.GetFiles(_temp.FullPath, "ai-providers.json"));
        Assert.Single(Directory.GetFiles(_temp.FullPath, "ai-providers.corrupt-*.json"));
    }

    [Fact]
    public async Task AtomicWrite_TmpFileRemovedAfterSave()
    {
        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());
        await store.SaveAsync(store.Snapshot());

        Assert.True(File.Exists(ProvidersPath));
        Assert.False(File.Exists(ProvidersPath + ".tmp"));
    }

    [Fact]
    public void Snapshot_NeverMutates_ReturnsSameInstanceUntilSave()
    {
        var store = new AiProviderProfileStore(ProvidersPath, LegacyOllama());
        var first = store.Snapshot();
        Assert.Same(first, store.Snapshot());
    }

    public void Dispose() => _temp.Dispose();
}
