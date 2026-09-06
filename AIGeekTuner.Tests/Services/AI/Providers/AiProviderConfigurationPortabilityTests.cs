using System.IO;
using System.Text.Json;
using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Configuration;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.AI.Providers;

public sealed class AiProviderConfigurationPortabilityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task Export_ContainsCatalogOnlyAndNeverReadsCredentials()
    {
        var credentials = new FakeAiCredentialStore();
        await credentials.SaveAsync("cloud", "sk-aigeek-secret-test-123");
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = "cloud",
            Profiles = [Cloud("cloud")]
        });
        var service = new AiProviderConfigurationPortabilityService(
            store,
            credentials,
            _temp.Combine("ai-providers.json"));
        var path = _temp.Combine("AIGeekTuner_AIProviders_v1.json");

        await service.ExportAsync(path);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("credentialsIncluded", json);
        Assert.Contains("false", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-aigeek-secret-test-123", json);
        Assert.Empty(credentials.LoadCalls);
    }

    [Fact]
    public async Task Import_MergesProfilesPreservesLocalProfilesAndCredentials()
    {
        var credentials = new FakeAiCredentialStore();
        await credentials.SaveAsync("cloud", "local-secret");
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = "local",
            Profiles = [Local("local"), Cloud("cloud", "旧名称")]
        });
        var service = new AiProviderConfigurationPortabilityService(
            store,
            credentials,
            _temp.Combine("ai-providers.json"));
        var path = _temp.Combine("import.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "credentialsIncluded": false,
          "activeProviderId": "cloud",
          "profiles": [
            {
              "id": "cloud",
              "displayName": "新名称",
              "kind": "openAiCompatible",
              "baseUrl": "http://127.0.0.1:1234/v1",
              "models": [{ "id": "model-new" }],
              "defaultModelId": "model-new",
              "structuredOutputMode": "openAiJsonSchema",
              "enabled": true
            },
            {
              "id": "new-provider",
              "displayName": "新 Provider",
              "kind": "openAiCompatible",
              "baseUrl": "https://example.invalid/v1",
              "models": [{ "id": "new-model" }],
              "defaultModelId": "new-model",
              "structuredOutputMode": "promptOnly",
              "enabled": true
            }
          ]
        }
        """);

        var result = await service.ImportAsync(path);

        Assert.Equal("cloud", result.ActiveProviderId);
        Assert.Equal(3, store.Snapshot().Profiles.Count);
        Assert.Equal("新名称", store.Snapshot().Profiles.Single(profile => profile.Id == "cloud").DisplayName);
        Assert.NotNull(store.Snapshot().Profiles.SingleOrDefault(profile => profile.Id == "local"));
        Assert.Contains("new-provider", result.MissingCredentialProviderIds);
        Assert.Equal("local-secret", credentials.Secrets["cloud"]);
        Assert.Contains("local-secret", credentials.Secrets.Values);
    }

    [Fact]
    public async Task Import_InvalidSchemaOrCredentialFlagDoesNotSave()
    {
        var credentials = new FakeAiCredentialStore();
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            Profiles = [Local("local")]
        });
        var service = new AiProviderConfigurationPortabilityService(
            store,
            credentials,
            _temp.Combine("ai-providers.json"));
        var unsupported = _temp.Combine("unsupported.json");
        await File.WriteAllTextAsync(unsupported, "{ \"schemaVersion\": 9, \"profiles\": [] }");
        await Assert.ThrowsAsync<AiProviderPortabilityException>(() => service.ImportAsync(unsupported));
        Assert.Empty(store.SavedConfigurations);

        var secretFlag = _temp.Combine("secret-flag.json");
        await File.WriteAllTextAsync(secretFlag, "{ \"schemaVersion\": 1, \"credentialsIncluded\": true, \"profiles\": [] }");
        await Assert.ThrowsAsync<AiProviderPortabilityException>(() => service.ImportAsync(secretFlag));
        Assert.Empty(store.SavedConfigurations);
    }

    [Fact]
    public async Task Import_MissingOrInvalidActiveIdPreservesCurrentActive()
    {
        var credentials = new FakeAiCredentialStore();
        var store = new ScriptedProviderStore(new AiProviderConfiguration
        {
            ActiveProviderId = "local",
            Profiles = [Local("local")]
        });
        var service = new AiProviderConfigurationPortabilityService(
            store,
            credentials,
            _temp.Combine("ai-providers.json"));
        var path = _temp.Combine("active.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "credentialsIncluded": false,
          "activeProviderId": "missing",
          "profiles": []
        }
        """);

        var result = await service.ImportAsync(path);

        Assert.Equal("local", result.ActiveProviderId);
        Assert.Equal("local", store.Snapshot().ActiveProviderId);
    }

    [Fact]
    public async Task Import_RealCatalogCreatesBackupAndDoesNotTouchCredentialFile()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        var store = new AiProviderProfileStore(
            paths.AiProvidersFilePath,
            new OllamaOptions { BaseUrl = "http://localhost:11434", ModelName = "qwen3:8b" });
        await store.SaveAsync(store.Snapshot());
        var credentials = new FakeAiCredentialStore();
        await credentials.SaveAsync("ollama", "sk-aigeek-secret-test-123");
        var service = new AiProviderConfigurationPortabilityService(
            store,
            credentials,
            paths.AiProvidersFilePath);
        var path = _temp.Combine("catalog.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "credentialsIncluded": false,
          "profiles": [
            {
              "id": "ollama",
              "displayName": "Ollama 新名称",
              "kind": "ollamaNative",
              "baseUrl": "http://localhost:11434",
              "models": [{ "id": "qwen3:8b" }],
              "defaultModelId": "qwen3:8b",
              "structuredOutputMode": "nativeSchema",
              "enabled": true
            }
          ]
        }
        """);

        await service.ImportAsync(path);

        Assert.Equal("Ollama 新名称", store.Snapshot().Profiles.Single().DisplayName);
        Assert.True(File.Exists(paths.AiProvidersFilePath + ".bak"));
        Assert.Equal("sk-aigeek-secret-test-123", credentials.Secrets["ollama"]);
        Assert.DoesNotContain("sk-aigeek-secret-test-123", await File.ReadAllTextAsync(paths.AiProvidersFilePath));
    }

    private static AiProviderProfile Local(string id) => new()
    {
        Id = id,
        DisplayName = "本地服务",
        Kind = AiProviderKind.OpenAiCompatible,
        BaseUrl = "http://127.0.0.1:1234/v1",
        Models = [new AiProviderModel("local-model")],
        DefaultModelId = "local-model",
        StructuredOutputMode = AiStructuredOutputMode.PromptOnly,
        Enabled = true
    };

    private static AiProviderProfile Cloud(string id, string? displayName = null) => new()
    {
        Id = id,
        DisplayName = displayName ?? "云端服务",
        Kind = AiProviderKind.OpenAiCompatible,
        BaseUrl = "https://example.invalid/v1",
        Models = [new AiProviderModel("cloud-model")],
        DefaultModelId = "cloud-model",
        StructuredOutputMode = AiStructuredOutputMode.OpenAiJsonSchema,
        Enabled = true
    };

    public void Dispose() => _temp.Dispose();
}
