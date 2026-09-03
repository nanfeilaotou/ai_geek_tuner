using System.IO;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Tests.TestSupport;
using Xunit;

namespace AIGeekTuner.Tests.Services.AI.Providers;

/// <summary>
/// Gate C/O：DPAPI 凭据存储（CurrentUser）。只在 Windows 上有意义，
/// 本仓库测试目标固定 net8.0-windows。
/// 安全红线：明文绝不落盘、绝不进异常/日志/快照断言消息。
/// </summary>
public sealed class WindowsDpapiCredentialStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string CredentialsPath => _temp.Combine("credentials.json");

    [Fact]
    public async Task Roundtrip_SaveThenLoad_ReturnsExactPlainText()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);

        await store.SaveAsync("my-provider", "sk-secret-value-123");

        Assert.Equal("sk-secret-value-123", await store.LoadAsync("my-provider"));
    }

    [Fact]
    public async Task Load_MissingProvider_ReturnsNull()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);

        Assert.Null(await store.LoadAsync("never-saved"));
    }

    [Fact]
    public async Task Replace_OverwritesPreviousSecret()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);
        await store.SaveAsync("my-provider", "first");

        await store.SaveAsync("my-provider", "second");

        Assert.Equal("second", await store.LoadAsync("my-provider"));
    }

    [Fact]
    public async Task Delete_RemovesEntry_SecondDeleteIsNoop()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);
        await store.SaveAsync("my-provider", "secret");

        await store.DeleteAsync("my-provider");

        Assert.Null(await store.LoadAsync("my-provider"));
        await store.DeleteAsync("my-provider");
        Assert.Null(await store.LoadAsync("my-provider"));
    }

    [Fact]
    public async Task CorruptBlob_LoadReturnsNull_NotThrow()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);
        await store.SaveAsync("my-provider", "secret");
        // 篡改 blob：合法 base64，但不是 DPAPI 可解的内容。
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var document = JsonSerializer.Deserialize<AiCredentialFileDocument>(
            await File.ReadAllTextAsync(CredentialsPath), options)!;
        Assert.NotEmpty(document.Credentials);
        document.Credentials[0].ProtectedBlobBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        await File.WriteAllTextAsync(CredentialsPath, JsonSerializer.Serialize(document, options));

        Assert.Null(await store.LoadAsync("my-provider"));
    }

    [Fact]
    public async Task CorruptFile_IsBackedUp_AndTreatedAsEmpty()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);
        await store.SaveAsync("my-provider", "secret");
        await File.WriteAllTextAsync(CredentialsPath, "definitely not json");

        var reloaded = new WindowsDpapiCredentialStore(CredentialsPath);

        Assert.Null(await reloaded.LoadAsync("my-provider"));
        Assert.Single(Directory.GetFiles(_temp.FullPath, "credentials.corrupt-*.json"));
    }

    [Fact]
    public async Task PlaintextAndBase64_NeverAppearOnDisk()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);
        var secret = "sk-plain-text-red-line";
        await store.SaveAsync("my-provider", secret);
        await store.SaveAsync("other", "another-plain-secret");

        var raw = await File.ReadAllTextAsync(CredentialsPath);

        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("another-plain-secret", raw);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), raw);
        var document = JsonSerializer.Deserialize<AiCredentialFileDocument>(raw)!;
        Assert.All(document.Credentials, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.ProviderId));
            Assert.False(string.IsNullOrWhiteSpace(entry.ProtectedBlobBase64));
        });
    }

    [Fact]
    public async Task Save_EmptySecret_Rejected()
    {
        var store = new WindowsDpapiCredentialStore(CredentialsPath);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("x", ""));
    }

    public void Dispose() => _temp.Dispose();
}
