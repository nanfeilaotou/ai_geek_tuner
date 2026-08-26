using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.AI;
using AIGeekTuner.Services.Diagnosis;

namespace AIGeekTuner.Tests.Services.AI;

/// <summary>
/// “保存设置后下一次诊断立即生效；进行中的诊断保持开始时快照”的确定性验证。
/// </summary>
public class SettingsImmediateApplyTests
{
    private static DiagnosticConfiguration Configuration(
        string baseUrl = "http://primary:11434",
        string modelName = OllamaOptions.DefaultModelName,
        bool useJsonFormat = true,
        int timeoutSeconds = OllamaOptions.DefaultTimeoutSeconds,
        int maxLogLength = DiagnosisInputOptions.DefaultMaxFaultLogCharacters)
    {
        return new DiagnosticConfiguration(
            new OllamaOptions
            {
                BaseUrl = baseUrl,
                ModelName = modelName,
                UseJsonFormat = useJsonFormat,
                TimeoutSeconds = timeoutSeconds
            },
            new DiagnosisInputOptions { MaxFaultLogCharacters = maxLogLength });
    }

    [Fact]
    public async Task BaseUrlChange_AppliesOnNextRequest()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var store = new DiagnosticConfigurationStore(Configuration());
        var service = server.CreateServiceWith(store);

        await service.GetDiagnosticResultAsync("system", "user");
        store.Replace(SnapshotFor(store, baseUrl: "http://secondary:11434"));
        await service.GetDiagnosticResultAsync("system", "user");

        Assert.EndsWith("/api/chat", server.RequestUrls[0]);
        Assert.Contains("primary", server.RequestUrls[0]);
        Assert.Contains("secondary", server.RequestUrls[1]);
    }

    [Fact]
    public async Task ModelAndJsonFormat_AppliedOnNextRequestBody()
    {
        var server = new StubOllamaServer();
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var store = new DiagnosticConfigurationStore(Configuration(useJsonFormat: true));
        var service = server.CreateServiceWith(store);

        await service.GetDiagnosticResultAsync("system", "user");
        store.Replace(SnapshotFor(store, modelName: "llama3.1:8b", useJsonFormat: false));
        await service.GetDiagnosticResultAsync("system", "user");

        using var firstBody = JsonDocument.Parse(server.RequestBodies[0]!);
        using var secondBody = JsonDocument.Parse(server.RequestBodies[1]!);
        Assert.Equal(OllamaOptions.DefaultModelName, firstBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("json", firstBody.RootElement.GetProperty("format").GetString());
        Assert.Equal("llama3.1:8b", secondBody.RootElement.GetProperty("model").GetString());
        Assert.False(secondBody.RootElement.TryGetProperty("format", out _));
    }

    [Fact]
    public async Task TimeoutChange_TakesEffectPerRequest()
    {
        var slowServer = new StubOllamaServer();
        // 3 秒后才返回合法 JSON；若新 Timeout 生效，请求会在约 1 秒超时。
        slowServer.EnqueueDelayedTextResponse(
            StubOllamaServer.ValidDiagnosticJson,
            TimeSpan.FromSeconds(3));
        var store = new DiagnosticConfigurationStore(Configuration(timeoutSeconds: 300));
        var service = slowServer.CreateServiceWith(store);

        store.Replace(SnapshotFor(store, timeoutSeconds: 1));
        await Assert.ThrowsAnyAsync<TimeoutException>(
            () => service.GetDiagnosticResultAsync("system", "user"));
    }

    [Fact]
    public void MaxLogLengthChange_NextPromptUsesNewLimit()
    {
        var store = new DiagnosticConfigurationStore(
            Configuration(maxLogLength: 1200));
        var builder = new DiagnosisPromptBuilder(() => store.Snapshot().Input);
        var longLog = new string('A', 1300) + "TAIL";
        var request = CreateRequest(longLog);

        var truncated = builder.BuildUserContext(request);
        store.Replace(SnapshotFor(store, maxLogLength: 5000));
        var notTruncated = builder.BuildUserContext(request);

        Assert.True(GetWasTruncated(truncated));
        Assert.False(GetWasTruncated(notTruncated));
    }

    [Fact]
    public async Task InFlightDiagnosis_KeepsOriginalSnapshot_WhenSettingsReplacedMidFlight()
    {
        var server = new StubOllamaServer();
        var gate = server.GateResponseBody(StubOllamaServer.ValidDiagnosticJson);
        server.EnqueueTextResponse(StubOllamaServer.ValidDiagnosticJson);
        var store = new DiagnosticConfigurationStore(Configuration(modelName: "model-a"));
        var service = server.CreateServiceWith(store);

        var inFlight = service.GetDiagnosticResultAsync("system", "user");

        // 等待第一次请求确实发出（不使用固定 Sleep 猜测）。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (server.RequestBodies.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Single(server.RequestBodies);

        // 请求在途期间替换为完全不同的配置。
        store.Replace(SnapshotFor(store, modelName: "model-b"));
        gate.Release();

        var result = await inFlight;
        Assert.Single(result.Evidence);

        using var firstBody = JsonDocument.Parse(server.RequestBodies[0]!);
        Assert.Equal("model-a", firstBody.RootElement.GetProperty("model").GetString());

        await service.GetDiagnosticResultAsync("system", "user");
        using var secondBody = JsonDocument.Parse(server.RequestBodies[1]!);
        Assert.Equal("model-b", secondBody.RootElement.GetProperty("model").GetString());
    }

    private static DiagnosticConfiguration SnapshotFor(
        DiagnosticConfigurationStore store,
        string? baseUrl = null,
        string? modelName = null,
        bool? useJsonFormat = null,
        int? timeoutSeconds = null,
        int? maxLogLength = null)
    {
        var current = store.Snapshot();
        return new DiagnosticConfiguration(
            new OllamaOptions
            {
                BaseUrl = baseUrl ?? current.Ollama.BaseUrl,
                ModelName = modelName ?? current.Ollama.ModelName,
                UseJsonFormat = useJsonFormat ?? current.Ollama.UseJsonFormat,
                TimeoutSeconds = timeoutSeconds ?? current.Ollama.TimeoutSeconds
            },
            new DiagnosisInputOptions
            {
                MaxFaultLogCharacters = maxLogLength ?? current.Input.MaxFaultLogCharacters
            });
    }

    private static DiagnosticRequest CreateRequest(string content) =>
        new()
        {
            Hardware = new AIGeekTuner.Models.HardwareInfo
            {
                CpuName = "TEST",
                DetectedAt = DateTimeOffset.UtcNow
            },
            FaultLog = AIGeekTuner.Models.FaultLog.FromPastedText(content)
        };

    private static bool GetWasTruncated(string userContext)
    {
        using var document = JsonDocument.Parse(userContext[userContext.IndexOf('{')..]);
        return document.RootElement.GetProperty("faultLog").GetProperty("wasTruncated").GetBoolean();
    }
}
