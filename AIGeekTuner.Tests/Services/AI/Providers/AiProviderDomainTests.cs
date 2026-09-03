using AIGeekTuner.Services.AI.Providers;
using AIGeekTuner.Services.AI.Providers.Transport;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers;

/// <summary>Gate A/O：Provider 领域规则（稳定 ID、校验、模型去重、URL 规范化）。</summary>
public sealed class AiProviderDomainTests
{
    [Theory]
    [InlineData("ollama")]
    [InlineData("lmstudio")]
    [InlineData("my-provider-2")]
    [InlineData("a")]
    public void ProviderId_ValidLowercaseAsciiDigitHyphen_Accepted(string value)
    {
        Assert.True(AiProviderId.TryNormalize(value, out var normalized));
        Assert.Equal(value, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Ollama")]
    [InlineData("my_provider")]
    [InlineData("-lead")]
    [InlineData("trail-")]
    [InlineData("with space")]
    public void ProviderId_InvalidForms_Rejected(string value)
    {
        Assert.False(AiProviderId.TryNormalize(value, out _));
    }

    [Fact]
    public void ProviderId_TooLong_Rejected()
    {
        Assert.False(AiProviderId.TryNormalize(new string('a', 65), out _));
        Assert.True(AiProviderId.TryNormalize(new string('a', 64), out _));
    }

    private static AiProviderProfile ValidProfile() => new()
    {
        Id = "ollama",
        DisplayName = "Ollama",
        Kind = AiProviderKind.OllamaNative,
        BaseUrl = "http://localhost:11434",
        Models = new[] { new AiProviderModel("qwen3:8b") },
        DefaultModelId = "qwen3:8b",
        StructuredOutputMode = AiStructuredOutputMode.NativeSchema
    };

    [Fact]
    public void Validate_ValidProfile_NoErrors()
    {
        Assert.Empty(AiProviderProfileValidator.Validate(ValidProfile()));
    }

    [Fact]
    public void Validate_LocalhostHttp_IsValid()
    {
        var local = new AiProviderProfile
        {
            Id = "local",
            DisplayName = "本地",
            Kind = AiProviderKind.OpenAiCompatible,
            BaseUrl = "http://127.0.0.1:1234/v1",
            StructuredOutputMode = AiStructuredOutputMode.PromptOnly
        };
        Assert.Empty(AiProviderProfileValidator.Validate(local));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("localhost:11434")]
    [InlineData("http://host/?x=1")]
    [InlineData("http://user:pass@host/v1")]
    public void Validate_InvalidBaseUrl_Reported(string baseUrl)
    {
        var profile = new AiProviderProfile
        {
            Id = "x",
            DisplayName = "X",
            Kind = AiProviderKind.OpenAiCompatible,
            BaseUrl = baseUrl,
            StructuredOutputMode = AiStructuredOutputMode.PromptOnly
        };
        Assert.Contains(AiProviderProfileValidator.Validate(profile), error => error.Contains("服务地址无效"));
    }

    [Fact]
    public void Validate_DuplicateModels_Reported_And_TrimmedIds_Compared()
    {
        var profile = ValidProfile();
        var duplicated = new AiProviderProfile
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            Kind = profile.Kind,
            BaseUrl = profile.BaseUrl,
            Models = new[]
            {
                new AiProviderModel(" qwen3:8b "),
                new AiProviderModel("qwen3:8b")
            },
            DefaultModelId = "qwen3:8b",
            StructuredOutputMode = profile.StructuredOutputMode
        };
        Assert.Contains(AiProviderProfileValidator.Validate(duplicated), error => error.Contains("重复的模型 ID"));
    }

    [Fact]
    public void Validate_DefaultModelNotInList_Reported()
    {
        var profile = ValidProfile();
        var mismatched = new AiProviderProfile
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            Kind = profile.Kind,
            BaseUrl = profile.BaseUrl,
            Models = profile.Models,
            DefaultModelId = "missing-model",
            StructuredOutputMode = profile.StructuredOutputMode
        };
        Assert.Contains(AiProviderProfileValidator.Validate(mismatched), error => error.Contains("不在模型列表中"));
    }

    [Fact]
    public void DisplayName_CanBeChanged_WithoutTouchingIdentity()
    {
        var original = ValidProfile();
        var renamed = new AiProviderProfile
        {
            Id = original.Id,
            DisplayName = "全新显示名",
            Kind = original.Kind,
            BaseUrl = original.BaseUrl,
            Models = original.Models,
            DefaultModelId = original.DefaultModelId,
            StructuredOutputMode = original.StructuredOutputMode
        };
        Assert.Equal(original.Id, renamed.Id);
        Assert.NotEqual(original.DisplayName, renamed.DisplayName);
    }
}

/// <summary>Gate E：BaseUrl（API 根地址）规范化。</summary>
public sealed class AiEndpointUriBuilderTests
{
    [Fact]
    public void LmStudioStyle_RootWithV1_ProducesModelsAndChatWithoutDuplication()
    {
        Assert.True(AiEndpointUriBuilder.TryCreateModelsUri("http://127.0.0.1:1234/v1", out var models));
        Assert.Equal("http://127.0.0.1:1234/v1/models", models.ToString());

        Assert.True(AiEndpointUriBuilder.TryCreateChatCompletionsUri("http://127.0.0.1:1234/v1/", out var chat));
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", chat.ToString());
    }

    [Fact]
    public void TrailingSlash_IsNormalized_NotDoubled()
    {
        Assert.True(AiEndpointUriBuilder.TryCreateModelsUri("http://localhost:11434///", out var uri));
        Assert.Equal("http://localhost:11434/models", uri.ToString());
    }

    [Fact]
    public void RootWithoutV1_IsNeverAutoAppendedV1()
    {
        Assert.True(AiEndpointUriBuilder.TryCreateModelsUri("http://example.com", out var uri));
        Assert.Equal("http://example.com/models", uri.ToString());
    }

    [Fact]
    public void OllamaApiPath_BuildsApiTagsAndApiChat()
    {
        Assert.True(AiEndpointUriBuilder.TryCreateApiPathUri("http://localhost:11434", "api/tags", out var tags));
        Assert.Equal("http://localhost:11434/api/tags", tags.ToString());

        Assert.True(AiEndpointUriBuilder.TryCreateApiPathUri("http://localhost:11434/", "api/chat", out var chat));
        Assert.Equal("http://localhost:11434/api/chat", chat.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not a url")]
    [InlineData("ftp://host/v1")]
    [InlineData("http://host/v1?x=1")]
    [InlineData("http://host/v1#frag")]
    public void InvalidRoots_Rejected(string? baseUrl)
    {
        Assert.False(AiEndpointUriBuilder.TryCreateRoot(baseUrl, out _));
    }
}
