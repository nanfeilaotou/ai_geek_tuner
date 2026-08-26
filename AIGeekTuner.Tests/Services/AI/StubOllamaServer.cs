using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using AIGeekTuner.Configuration;
using AIGeekTuner.Services.AI;

namespace AIGeekTuner.Tests.Services.AI;

/// <summary>
/// 确定性本地 HttpMessageHandler 替身：按脚本依次返回响应，
/// 并记录每个请求的 URL 与请求体，供断言最终序列化内容。
/// </summary>
public sealed class StubOllamaServer : HttpMessageHandler
{
    private readonly List<Func<int, HttpRequestMessage, HttpResponseMessage>> _script = [];
    private int _callIndex;

    public List<string?> RequestUrls { get; } = [];
    public List<string?> RequestBodies { get; } = [];

    /// <summary>
    /// 一份能通过 Parser 全部校验的最小诊断 JSON，供“单次成功”类测试复用。
    /// </summary>
    public const string ValidDiagnosticJson = """
{
  "summary": "当前迹象更符合内存稳定性问题，仍需复测确认",
  "rootCause": "已有证据：TM5 报错；不确定因素：缺少复测数据。无法确定，需要进一步测试",
  "confidence": 0.55,
  "riskLevel": "Medium",
  "evidence": [
    { "kind": "Fact", "description": "日志中记录 TM5 Error 2" }
  ],
  "recommendations": [
    {
      "action": "重新运行内存稳定性测试并记录错误",
      "reason": "用于确认问题是否可复现",
      "riskLevel": "Low",
      "precautions": ["保存当前配置"]
    }
  ]
}
""";


    public void EnqueueTextResponse(string content)
    {
        _script.Add((_, _) => CreateChatResponse(content));
    }

    public void EnqueueHttpStatus(HttpStatusCode statusCode, string body)
    {
        _script.Add((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    /// <summary>
    /// 响应头立即返回，但响应体在 Release 前不可读：
    /// 用于验证“请求已在线上时配置被替换”的场景，全程不阻塞线程池。
    /// </summary>
    public ResponseGate GateResponseBody(string content)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _script.Add((_, _) =>
        {
            var payload = Encoding.UTF8.GetBytes(BuildChatResponse(content));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedReadStream(gate.Task, payload))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        });
        return new ResponseGate(gate);
    }

    public sealed class ResponseGate
    {
        private readonly TaskCompletionSource _source;

        internal ResponseGate(TaskCompletionSource source)
        {
            _source = source;
        }

        public void Release() => _source.TrySetResult();
    }

    /// <summary>一次性只读流：Release 前读不到任何字节，Release 后吐出全部负载。</summary>
    private sealed class GatedReadStream : Stream
    {
        private readonly Task _gate;
        private readonly byte[] _payload;
        private bool _delivered;

        public GatedReadStream(Task gate, byte[] payload)
        {
            _gate = gate;
            _payload = payload;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _gate.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_delivered || buffer.Length == 0)
            {
                return 0;
            }

            _delivered = true;
            var count = Math.Min(buffer.Length, _payload.Length);
            _payload.AsSpan(0, count).CopyTo(buffer.Span);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    public void EnqueueThrow(Exception exception)
    {
        _script.Add((_, _) => throw exception);
    }

    /// <summary>返回 /api/tags 形态的原始 JSON。</summary>
    public void EnqueueTags(string rawJson)
    {
        _script.Add((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        });
    }

    /// <summary>响应体在 Release 前不可读的任意原始 JSON（用于门控 /api/tags）。</summary>
    public ResponseGate GateRawResponse(string rawJson)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _script.Add((_, _) =>
        {
            var payload = Encoding.UTF8.GetBytes(rawJson);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedReadStream(gate.Task, payload))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        });
        return new ResponseGate(gate);
    }

    /// <summary>连接检测服务：共享同一确定性脚本。</summary>
    public OllamaConnectionService CreateConnectionService(int checkTimeoutSeconds = 10)
    {
        var client = new HttpClient(this);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return new OllamaConnectionService(client, checkTimeoutSeconds);
    }

    public void OnFirstRequest(Action action)
    {
        _script.Add((index, _) =>
        {
            if (index == 0)
            {
                action();
            }

            return CreateChatResponse("irrelevant");
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var index = _callIndex++;
        RequestUrls.Add(request.RequestUri?.ToString());
        RequestBodies.Add(request.Content?
            .ReadAsStringAsync(cancellationToken)
            .GetAwaiter()
            .GetResult());

        var responder = index < _script.Count
            ? _script[index]
            : throw new InvalidOperationException($"收到了脚本之外的第 {index + 1} 次请求。");
        return Task.FromResult(responder(index, request));
    }

    public HttpClient CreateClient()
    {
        var client = new HttpClient(this);
        // 与生产一致：由 OllamaOptions.TimeoutSeconds 控制超时。
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    public OllamaService CreateService(OllamaOptions? options = null)
    {
        return new OllamaService(
            CreateClient(),
            options ?? new OllamaOptions());
    }

    /// <summary>以配置存储驱动服务：每次请求从 store 取快照。</summary>
    public OllamaService CreateServiceWith(
        DiagnosticConfigurationStore store)
    {
        var client = new HttpClient(this);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return new OllamaService(client, () => store.Snapshot().Ollama);
    }

    /// <summary>延迟指定时间后才返回，用于验证超时语义。</summary>
    public void EnqueueDelayedTextResponse(string content, TimeSpan delay)
    {
        _script.Add((_, _) =>
        {
            Thread.Sleep(delay);
            return CreateChatResponse(content);
        });
    }

    public static string BuildChatResponse(string assistantContent)
    {
        // 使用 C# 原始字符串字面量，避免手工转义 JSON 引号。
        return $$"""
{"message":{"role":"assistant","content":{{JsonSerializer.Serialize(assistantContent)}}},"done":true}
""";
    }

    private HttpResponseMessage CreateChatResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                BuildChatResponse(content),
                Encoding.UTF8,
                "application/json")
        };
    }
}
