using AIGeekTuner.Configuration;
using System.Text.Json;
using System.Net.Http;
using System.Net;
using AIGeekTuner.Services.SessionAnalysis;
using AIGeekTuner.Services.Telemetry.Recording;
using Xunit;

namespace AIGeekTuner.Tests.Services.SessionAnalysis
{
    /// <summary>§2/§3/§6：Session 分析请求体必须显式 think:false，且保持 schema 与输出预算。</summary>
    public class SessionAnalysisThinkRequestTests
    {
        private const string ValidEvidenceA = "stat:cpu:cpu.package.temperature";
        private const string ValidEvidenceB = "event:0001";

        private static string ValidJson() =>
            "{\"summary\":\"s\",\"overallAssessment\":\"Normal\",\"confidence\":0.8,"
            + "\"findings\":[],\"recommendations\":[],\"uncertainties\":[],\"spokenSummary\":\"ok\"}";

        private static TelemetrySessionAnalyzer.TelemetryAnalysisContext Context() =>
            new(
                new TelemetrySessionAnalyzer.AnalysisMetadata("sid", T0, TimeSpan.FromSeconds(30), 15, 2000),
                [new TelemetrySessionAnalyzer.AnalysisSource("HwInfo", "Ready", 3)],
                [new TelemetrySessionAnalyzer.AnalysisStatistic(
                    ValidEvidenceA, "cpu", "cpu.package.temperature", "Celsius",
                    15, 100, 70, 72, 74, 74)],
                [
                    new TelemetrySessionAnalyzer.AnalysisEvent(
                        ValidEvidenceB, "SignificantChange", T0.AddSeconds(10), "HwInfo",
                        "gpu.core.clock", "2400 MHz", "210 MHz", "clock drop"),
                ]);

        private static readonly DateTimeOffset T0 =
            new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task RequestBody_ExplicitlyDisablesThinking_KeepsSchemaAndBudget()
        {
            string? capturedBody = null;
            var httpClient = new HttpClient(new CapturingHandler(async (request, ct) =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(ValidJson()) + "}}"),
                };
            }));
            var realClient = new OllamaChatClient(
                httpClient,
                () => new OllamaOptions { ModelName = "qwen3:8b", TimeoutSeconds = 30 });
            var service = new OllamaSessionAnalysisService(realClient, new SessionAnalysisPromptBuilder());

            _ = await service.AnalyzeAsync(Context(), CancellationToken.None);

            Assert.NotNull(capturedBody);
            Assert.Contains("\"think\":false", capturedBody);
            Assert.Contains("\"num_predict\":2048", capturedBody);
            Assert.Contains("\"format\":{", capturedBody);
        }

        private sealed class CapturingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => handler(request, cancellationToken);
        }
    }
}


