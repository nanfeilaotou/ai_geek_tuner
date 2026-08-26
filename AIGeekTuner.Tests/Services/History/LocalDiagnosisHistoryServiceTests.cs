using System.IO;
using System.Text;
using System.Text.Json;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Diagnosis;
using AIGeekTuner.Services.History;
using AIGeekTuner.Tests.TestSupport;

namespace AIGeekTuner.Tests.Services.History;

/// <summary>
/// LocalDiagnosisHistoryService V1 存储层契约：
/// 成功/失败双轨保存、执行元数据、旧格式兼容、损坏索引自愈、
/// 原子写残留检查、ClearAll 边界与并发一致性。
/// </summary>
public class LocalDiagnosisHistoryServiceTests : IDisposable
{
    private const string TestModel = "qwen3:8b-test";

    private readonly TempDirectory _temp = new();
    private readonly LocalDiagnosisHistoryService _service;

    public LocalDiagnosisHistoryServiceTests()
    {
        _service = new LocalDiagnosisHistoryService(HistoryTestFactory.CreatePaths(_temp));
    }

    // ---- Save / 元数据 ----

    [Fact]
    public async Task SaveSuccess_WritesDetailAndIndex_WithExecutionMetadata()
    {
        var outcome = HistoryTestFactory.CreateOutcome();

        var record = await _service.SaveSuccessAsync(
            outcome, TestModel, durationMs: 1234, CancellationToken.None);

        Assert.True(record.Succeeded);
        Assert.Equal(TestModel, record.ModelName);
        Assert.Equal(1234, record.DurationMs);
        Assert.Null(record.FailureReason);
        Assert.Equal(outcome.DiagnosisId, record.DiagnosisId);

        var records = await _service.GetRecordsAsync(CancellationToken.None);
        var stored = Assert.Single(records);
        Assert.True(stored.Succeeded);
        Assert.Equal(TestModel, stored.ModelName);
        Assert.True(File.Exists(stored.DiagnosisOutcomePath));
    }

    [Fact]
    public async Task LoadDetail_Success_RestoresOutcomeAndMetadata()
    {
        var outcome = HistoryTestFactory.CreateOutcome(logContent: "原始日志内容 ABC-123");
        var record = await _service.SaveSuccessAsync(
            outcome, TestModel, 987, CancellationToken.None);

        var detail = await _service.LoadDetailAsync(record, CancellationToken.None);

        Assert.True(detail.Succeeded);
        Assert.Equal(TestModel, detail.ModelName);
        Assert.Equal(987, detail.DurationMs);
        Assert.NotNull(detail.Outcome);
        Assert.Equal("原始日志内容 ABC-123", detail.Outcome!.Request.FaultLog.Content);
        Assert.Equal("TEST-CPU", detail.Outcome.Request.Hardware.CpuName);
    }

    [Fact]
    public async Task SaveTwoRecords_IndexOrdersNewestFirst()
    {
        await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(summary: "较早",
                completedAt: new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)),
            TestModel,
            100,
            CancellationToken.None);
        await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(summary: "较晚",
                completedAt: new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero)),
            TestModel,
            200,
            CancellationToken.None);

        var records = await _service.GetRecordsAsync(CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.Equal("较晚", records[0].Summary);
        Assert.Equal("较早", records[1].Summary);
    }

    [Fact]
    public async Task Delete_RemovesRecordAndDetail_KeepsOthersIntact()
    {
        var first = await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(summary: "第一条"), TestModel, 10, CancellationToken.None);
        var second = await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(summary: "第二条"), TestModel, 20, CancellationToken.None);

        await _service.DeleteAsync(first.DiagnosisId, CancellationToken.None);

        var remaining = Assert.Single(await _service.GetRecordsAsync(CancellationToken.None));
        Assert.Equal(second.DiagnosisId, remaining.DiagnosisId);
        Assert.False(File.Exists(first.DiagnosisOutcomePath));
        Assert.True(File.Exists(second.DiagnosisOutcomePath));

        var restored = await _service.LoadDetailAsync(second, CancellationToken.None);
        Assert.Equal("第二条", restored.Outcome!.AiResult.Summary);
    }

    [Fact]
    public async Task Delete_UnknownId_CompletesWithoutChangingRecords()
    {
        await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(), TestModel, 5, CancellationToken.None);

        await _service.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Single(await _service.GetRecordsAsync(CancellationToken.None));
    }

    // ---- 失败记录 ----

    [Fact]
    public async Task SaveFailure_WritesFailedRecord_WithoutFakeOutcome()
    {
        var failure = new DiagnosisFailureInfo(
            DateTimeOffset.Now,
            TestModel,
            15200,
            FailureCode: nameof(DiagnosisError.AiResponseInvalid),
            FailureReason: "本地 AI 返回的诊断结果无法解析。",
            LogFileName: "tm5-error.log");

        var record = await _service.SaveFailureAsync(failure, CancellationToken.None);

        Assert.False(record.Succeeded);
        Assert.Equal(TestModel, record.ModelName);
        Assert.Equal(15200, record.DurationMs);
        Assert.Contains("无法解析", record.Summary);
        Assert.Equal(SafetyStatus.Pending, record.SafetyStatus);

        var detail = await _service.LoadDetailAsync(record, CancellationToken.None);
        Assert.False(detail.Succeeded);
        Assert.Null(detail.Outcome);
        Assert.Equal(nameof(DiagnosisError.AiResponseInvalid), detail.FailureCode);
        Assert.Contains("无法解析", detail.FailureReason);
    }

    [Fact]
    public async Task SaveFailure_TruncatesVeryLongReason()
    {
        var failure = new DiagnosisFailureInfo(
            DateTimeOffset.Now, TestModel, 10, "X", new string('长', 500), null);

        var record = await _service.SaveFailureAsync(failure, CancellationToken.None);

        Assert.True(record.Summary.Length <= 301);
    }

    // ---- 兼容：旧版直接序列化 DiagnosisOutcome 的详情 ----

    [Fact]
    public async Task LegacyOutcomeDetail_LoadsWithUnknownModelAndDuration()
    {
        var outcome = HistoryTestFactory.CreateOutcome();
        var paths = HistoryTestFactory.CreatePaths(_temp);
        Directory.CreateDirectory(paths.HistoryDirectory);
        var detailPath = Path.Combine(paths.HistoryDirectory, outcome.DiagnosisId.ToString("N") + ".json");
        var legacyJson = JsonSerializer.Serialize(outcome, JsonOptionsForLegacy());
        await File.WriteAllTextAsync(detailPath, legacyJson);
        var utc = outcome.CompletedAt.ToUniversalTime().ToString("O");
        var indexJson = $@"[{{""diagnosisId"":""{outcome.DiagnosisId}"",""createdAt"":""{utc}"",""logFileName"":""legacy.log"",""summary"":""旧摘要"",""riskLevel"":""Low"",""confidence"":0.4,""safetyStatus"":""Approved"",""diagnosisOutcomePath"":{JsonSerializer.Serialize(detailPath)}}}]";
        await File.WriteAllTextAsync(Path.Combine(paths.HistoryDirectory, "records.json"), indexJson);

        var records = await _service.GetRecordsAsync(CancellationToken.None);
        var record = Assert.Single(records);
        Assert.True(record.Succeeded);          // 旧记录默认按成功处理
        Assert.Null(record.ModelName);          // 不伪造模型名
        Assert.Null(record.DurationMs);         // 不伪造耗时
        Assert.Equal("未知", record.ModelDisplay);
        Assert.Equal("未知", record.DurationDisplay);

        var detail = await _service.LoadDetailAsync(record, CancellationToken.None);
        Assert.True(detail.Succeeded);
        Assert.NotNull(detail.Outcome);
        Assert.Null(detail.ModelName);
    }

    // ---- 损坏自愈 ----

    [Fact]
    public async Task GetRecords_CorruptIndex_IsBackedUpAndRebuiltFromValidDetails()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        Directory.CreateDirectory(paths.HistoryDirectory);

        var validOutcomeId = Guid.NewGuid();
        var failureId = Guid.NewGuid();
        var brokenId = Guid.NewGuid();

        var validDetail = MakeSuccessEnvelope(validOutcomeId);
        var failureDetail = new DiagnosisHistoryDetail
        {
            SchemaVersion = 2,
            Succeeded = false,
            FailureReason = "调用超时",
            FailureCode = "AiRequestFailed",
            ModelName = TestModel,
            DurationMs = 30000,
            CompletedAt = DateTimeOffset.Now
        };
        await File.WriteAllTextAsync(
            Path.Combine(paths.HistoryDirectory, validOutcomeId.ToString("N") + ".json"),
            JsonSerializer.Serialize(validDetail, JsonOptionsForLegacy()));
        await File.WriteAllTextAsync(
            Path.Combine(paths.HistoryDirectory, failureId.ToString("N") + ".json"),
            JsonSerializer.Serialize(failureDetail, JsonOptionsForLegacy()));
        await File.WriteAllTextAsync(
            Path.Combine(paths.HistoryDirectory, brokenId.ToString("N") + ".json"),
            "{ broken");
        await File.WriteAllTextAsync(
            Path.Combine(paths.HistoryDirectory, "records.corrupt-old.json"),
            "[garbage]");
        await File.WriteAllTextAsync(
            Path.Combine(paths.HistoryDirectory, "stray.tmp"),
            "{}");
        await File.WriteAllTextAsync(
            Path.Combine(paths.HistoryDirectory, "records.json"),
            "{ malformed index");

        var records = await _service.GetRecordsAsync(CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.DiagnosisId == validOutcomeId && r.Succeeded);
        Assert.Contains(records, r => r.DiagnosisId == failureId && !r.Succeeded);
        Assert.DoesNotContain(records, r => r.DiagnosisId == brokenId);

        var directory = paths.HistoryDirectory;
        var backups = Directory.GetFiles(directory, "records.corrupt-*.json");
        Assert.Single(backups,
            file => !file.EndsWith("-old.json", StringComparison.Ordinal)); // 仅本次新生成的坏索引备份
        Assert.True(File.Exists(Path.Combine(directory, "records.json")));

        var second = await _service.GetRecordsAsync(CancellationToken.None);
        Assert.Equal(2, second.Count);
        Assert.Single(Directory.GetFiles(directory, "records.corrupt-*.json"),
            file => !file.EndsWith("-old.json", StringComparison.Ordinal));

        Assert.DoesNotContain(records, r => r.DiagnosisOutcomePath.EndsWith(".tmp", StringComparison.Ordinal));
    }

    // ---- 原子写 ----

    [Fact]
    public async Task Save_AfterCompletion_NoTmpLeftover_AndIndexParsesCleanly()
    {
        await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(), TestModel, 42, CancellationToken.None);

        var directory = Path.GetDirectoryName(
            (await _service.GetRecordsAsync(CancellationToken.None))[0].DiagnosisOutcomePath)!;
        var jsonFiles = Directory.GetFiles(directory, "*.json");
        Assert.Equal(2, jsonFiles.Length);
        Assert.All(jsonFiles, file =>
            Assert.False(file.EndsWith(".tmp", StringComparison.Ordinal)));

        using var index = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "records.json")));
        Assert.Single(index.RootElement.EnumerateArray());
    }

    // ---- 清空 ----

    [Fact]
    public async Task ClearAll_RemovesHistoryData_ButKeepsMarkerAndSiblingDirs()
    {
        var paths = HistoryTestFactory.CreatePaths(_temp);
        await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(), TestModel, 7, CancellationToken.None);
        await _service.SaveSuccessAsync(
            HistoryTestFactory.CreateOutcome(), TestModel, 8, CancellationToken.None);

        var markerPath = Path.Combine(paths.HistoryDirectory, ".legacy-history-imported");
        await File.WriteAllTextAsync(markerPath, "marker");
        Directory.CreateDirectory(paths.SettingsDirectory);
        var settingsFile = Path.Combine(paths.SettingsDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsFile, "{}");
        var logsDir = Path.Combine(paths.RootDirectory, "Logs");
        Directory.CreateDirectory(logsDir);
        var logFile = Path.Combine(logsDir, "app.log");
        await File.WriteAllTextAsync(logFile, "log");

        await _service.ClearAllAsync(CancellationToken.None);

        Assert.Empty(await _service.GetRecordsAsync(CancellationToken.None));
        // 仅保留旧版迁移标记
        var remaining = Directory.EnumerateFiles(paths.HistoryDirectory).ToArray();
        Assert.Single(remaining);
        Assert.EndsWith(".legacy-history-imported", remaining[0], StringComparison.Ordinal);
        Assert.True(File.Exists(settingsFile));
        Assert.True(File.Exists(logFile));
    }

    // ---- 并发 ----

    [Fact]
    public async Task Save_FiveConcurrentSaves_AllPersistedWithoutCorruption()
    {
        var saves = Enumerable.Range(0, 5)
            .Select(index => _service.SaveSuccessAsync(
                HistoryTestFactory.CreateOutcome(
                    summary: "并发诊断 " + index,
                    completedAt: new DateTimeOffset(2026, 1, 1, 9, index, 0, TimeSpan.Zero)),
                TestModel,
                index * 10,
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(saves);

        var records = await _service.GetRecordsAsync(CancellationToken.None);
        Assert.Equal(5, records.Count);
        Assert.Equal(5, records.Select(r => r.DiagnosisId).Distinct().Count());
        foreach (var record in records)
        {
            var detail = await _service.LoadDetailAsync(record, CancellationToken.None);
            Assert.True(detail.Succeeded);
        }
    }

    [Fact]
    public async Task ConcurrentSaveAndClear_IndexRemainsValid_AndContractIsExplainable()
    {
        var saves = Enumerable.Range(0, 4)
            .Select(index => _service.SaveSuccessAsync(
                HistoryTestFactory.CreateOutcome(summary: "并发 " + index),
                TestModel,
                index * 5,
                CancellationToken.None));
        var clear = _service.ClearAllAsync(CancellationToken.None);

        await Task.WhenAll(saves.Concat([clear]));

        var records = await _service.GetRecordsAsync(CancellationToken.None);
        Assert.All(records, record => Assert.True(File.Exists(record.DiagnosisOutcomePath)));
    }

    private static DiagnosisHistoryDetail MakeSuccessEnvelope(Guid id)
    {
        var template = HistoryTestFactory.CreateOutcome();
        return new DiagnosisHistoryDetail
        {
            SchemaVersion = 2,
            Succeeded = true,
            ModelName = TestModel,
            DurationMs = 555,
            Outcome = new DiagnosisOutcome
            {
                DiagnosisId = id,
                Request = template.Request,
                AiResult = template.AiResult,
                Safety = template.Safety,
                CompletedAt = template.CompletedAt
            }
        };
    }

    private static System.Text.Json.JsonSerializerOptions JsonOptionsForLegacy() => new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public void Dispose() => _temp.Dispose();
}
