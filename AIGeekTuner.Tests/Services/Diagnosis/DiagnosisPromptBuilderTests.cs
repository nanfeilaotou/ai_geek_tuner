using System.Text.Json;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Services.Diagnosis;

namespace AIGeekTuner.Tests.Services.Diagnosis;

/// <summary>
/// PromptBuilder 关键 invariant 安全网：不锁定整段提示词文本，
/// 只验证截断语义与“不得虚构输入”这两个核心行为。
/// 说明：userContext 是“前缀文字 + 单个 JSON 对象”；中文在 JSON 中会被默认编码器
/// 转义为 \uXXXX，因此所有内容级断言都在 JsonDocument 解码后的值上进行。
/// </summary>
public class DiagnosisPromptBuilderTests
{
    [Fact]
    public void BuildUserContext_ShortLog_IsNotMarkedTruncated()
    {
        var builder = new DiagnosisPromptBuilder();
        var request = CreateRequest(new string('L', 300) + "TAILMARK");

        var (content, wasTruncated) = ExtractFaultLog(builder.BuildUserContext(request));

        Assert.False(wasTruncated);
        Assert.EndsWith("TAILMARK", content);
        Assert.DoesNotContain("[日志已截断", content);
    }

    [Fact]
    public void BuildUserContext_OversizedLog_KeepsHeadAndTailAndMarksTruncation()
    {
        var builder = new DiagnosisPromptBuilder(new DiagnosisInputOptions
        {
            MaxFaultLogCharacters = 1200
        });

        var head = "HEADSTART" + new string('A', 500);
        var middle = new string('B', 300) + "MIDDLEUNIQUE7Q" + new string('B', 100);
        var tail = new string('C', 400) + "TAILEND";
        var request = CreateRequest(head + middle + tail);

        var (content, wasTruncated) = ExtractFaultLog(builder.BuildUserContext(request));

        // 头部与尾部保留、中间内容被移除
        Assert.True(wasTruncated);
        Assert.StartsWith("HEADSTART", content);
        Assert.EndsWith("TAILEND", content);
        Assert.DoesNotContain("MIDDLEUNIQUE7Q", content);
        // 截断事实显式告知模型，并带原始长度
        Assert.Contains("[日志已截断：原始长度 1330 个字符；仅保留开头和结尾，中间内容已省略。]", content);
    }

    [Fact]
    public void BuildUserContext_MissingHardwareFields_DoesNotInventSensorData()
    {
        var builder = new DiagnosisPromptBuilder();
        var request = CreateRequest("TM5 Error 2 memory instability");

        var userContext = builder.BuildUserContext(request);
        var (content, _) = ExtractFaultLog(userContext);

        // 日志原文必须完整在场
        Assert.Equal("TM5 Error 2 memory instability", content);
        using var document = JsonDocument.Parse(userContext[userContext.IndexOf('{')..]);
        var hardware = document.RootElement.GetProperty("hardware");
        // 未采集的字段保持 Unknown，而不是被默认值填充
        Assert.Equal("Unknown", hardware.GetProperty("cpuName").GetString());
        Assert.Equal("Unknown", hardware.GetProperty("memoryType").GetString());
        // 不存在任何温度、电压、功耗类字段——程序侧就不产生这类数据
        AssertDoesNotContainKey(hardware, "temperatureCelsius");
        AssertDoesNotContainKey(hardware, "voltage");
        AssertDoesNotContainKey(hardware, "powerWatts");
        // “温度”的 JSON 转义形态也不应出现（\u6E29\u5EA6）
        Assert.DoesNotContain("\\u6E29\\u5EA6", userContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("温度", userContext);
    }

    [Fact]
    public void BuildUserContext_MaxFaultLogCharactersBelowMinimum_ThrowsOnUse()
    {
        // 校验发生在每次构造上下文时（配置可经工厂动态更换），而非构造器。
        var builder = new DiagnosisPromptBuilder(new DiagnosisInputOptions
        {
            MaxFaultLogCharacters = 999
        });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.BuildUserContext(CreateRequest("log")));
    }

    private static (string Content, bool WasTruncated) ExtractFaultLog(string userContext)
    {
        using var document = JsonDocument.Parse(userContext[userContext.IndexOf('{')..]);
        var faultLog = document.RootElement.GetProperty("faultLog");
        return (
            faultLog.GetProperty("content").GetString()!,
            faultLog.GetProperty("wasTruncated").GetBoolean());
    }

    private static void AssertDoesNotContainKey(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            Assert.NotEqual(propertyName, property.Name, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static DiagnosticRequest CreateRequest(string faultLogContent)
    {
        return new DiagnosticRequest
        {
            Hardware = new HardwareInfo(),
            FaultLog = FaultLog.FromPastedText(faultLogContent)
        };
    }
}
