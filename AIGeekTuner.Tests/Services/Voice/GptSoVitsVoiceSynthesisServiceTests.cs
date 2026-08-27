using System.Net.Http;
using System.Net;
using System.Text;
using AIGeekTuner.Services.Voice;
using Xunit;

namespace AIGeekTuner.Tests.Services.Voice
{
    /// <summary>§13 GPT-SoVITS 客户端自动化测试：fake handler，不启动 Python。</summary>
    public class GptSoVitsVoiceSynthesisServiceTests
    {
        private static readonly byte[] Wav =
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E'];

        private static readonly VoiceConfiguration Config = new(
            "http://127.0.0.1:9880",
            "D:/gsv/ref.wav",
            "ref prompt",
            "ja",
            "zh",
            1.0,
            "D:/gsv/gpt.ckpt",
            "D:/gsv/sovits.pth");

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<(string Method, string Url, string? Body)> Requests { get; } = [];
            public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([82, 73, 70, 70]),
                };

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? null
                    : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
                return Task.FromResult(Responder(request));
            }
        }

        private static (GptSoVitsVoiceSynthesisService Service, RecordingHandler Handler) Create()
        {
            var handler = new RecordingHandler();
            return (new GptSoVitsVoiceSynthesisService(new HttpClient(handler)), handler);
        }

        [Fact]
        public async Task Synthesize_PostsToTts_WithAllRequiredFields()
        {
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Wav),
            };

            var result = await service.SynthesizeAsync("测试文本", Config, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(Wav, result.WavBytes);

            var tts = Assert.Single(handler.Requests, r => r.Url.EndsWith("/tts"));
            Assert.Equal("POST", tts.Method);
            Assert.NotNull(tts.Body);
            Assert.Contains("\"text\":\"测试文本\"", tts.Body);
            Assert.Contains("\"text_lang\":\"zh\"", tts.Body);
            Assert.Contains("\"ref_audio_path\":\"D:/gsv/ref.wav\"", tts.Body);
            Assert.Contains("\"prompt_text\":\"ref prompt\"", tts.Body);
            Assert.Contains("\"prompt_lang\":\"ja\"", tts.Body);
            Assert.Contains("\"speed_factor\":1", tts.Body);
            Assert.Contains("\"media_type\":\"wav\"", tts.Body);
            Assert.Contains("\"streaming_mode\":false", tts.Body);
        }

        [Fact]
        public async Task Weights_AppliedOnce_ForRepeatedSameConfig()
        {
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Wav),
            };

            _ = await service.SynthesizeAsync("一", Config, CancellationToken.None);
            _ = await service.SynthesizeAsync("二", Config, CancellationToken.None);
            _ = await service.SynthesizeAsync("三", Config, CancellationToken.None);

            Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("/set_gpt_weights")));
            Assert.Equal(1, handler.Requests.Count(r => r.Url.Contains("/set_sovits_weights")));
            Assert.Equal(3, handler.Requests.Count(r => r.Url.EndsWith("/tts")));
        }

        [Fact]
        public async Task WeightEndpoints_CarryEscapedPaths()
        {
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Wav),
            };

            await service.SynthesizeAsync("x", Config, CancellationToken.None);

            var gpt = handler.Requests.First(r => r.Url.Contains("/set_gpt_weights"));
            Assert.Contains("weights_path=" + Uri.EscapeDataString(Config.GptModelPath!), gpt.Url);
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public async Task HttpErrorStatus_Fails(HttpStatusCode status)
        {
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(status)
            {
                Content = new StringContent("boom"),
            };

            var result = await service.SynthesizeAsync("text", Config, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains(((int)status).ToString(), result.ErrorMessage);
        }

        [Fact]
        public async Task ConnectionFailure_FailsWithFriendlyMessage()
        {
            var handler = new FailingHandler(new HttpRequestException("connection refused"));
            var service = new GptSoVitsVoiceSynthesisService(new HttpClient(handler));

            var result = await service.SynthesizeAsync("text", Config, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("无法连接", result.ErrorMessage);
        }

        [Fact]
        public async Task Timeout_FailsWithTimeoutMessage()
        {
            var handler = new FailingHandler(new TaskCanceledException());
            var service = new GptSoVitsVoiceSynthesisService(new HttpClient(handler));

            var result = await service.SynthesizeAsync("text", Config, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("超时", result.ErrorMessage);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            var handler = new FailingHandler(new OperationCanceledException());
            var service = new GptSoVitsVoiceSynthesisService(new HttpClient(handler));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.SynthesizeAsync("text", Config, cts.Token));
        }

        [Fact]
        public async Task EmptyBody_Fails()
        {
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

            var result = await service.SynthesizeAsync("text", Config, CancellationToken.None);

            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task OversizedBody_Fails()
        {
            var big = new byte[25 * 1024 * 1024 + 1];
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(big),
            };

            var result = await service.SynthesizeAsync("text", Config, CancellationToken.None);

            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task UnexpectedContentType_Fails()
        {
            var (service, handler) = Create();
            handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>error page</html>", Encoding.UTF8, "text/html"),
            };

            var result = await service.SynthesizeAsync("text", Config, CancellationToken.None);

            Assert.False(result.Succeeded);
        }

        private sealed class FailingHandler(Exception exception) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromException<HttpResponseMessage>(exception);
        }
    }
}



