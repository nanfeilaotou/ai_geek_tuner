using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIGeekTuner.Tests.Services.AI.Providers;

/// <summary>
/// Provider 传输测试用的确定性 HttpMessageHandler 替身：
/// 按脚本返回响应，记录请求 URL / 方法 / Authorization 头 / 请求体。
/// 绝不访问真实网络（不真实调用任何 cloud API）。
/// </summary>
public sealed class StubAiHttpHandler : HttpMessageHandler
{
    private readonly List<Func<HttpRequestMessage, HttpResponseMessage>> _script = [];
    private int _callIndex;

    public List<(string Method, string Url, string? Authorization, string? Body)> Requests { get; } = [];

    public void Enqueue(HttpStatusCode statusCode, string body)
    {
        _script.Add(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _script.Add(responder);
    }

    public void EnqueueSuccessJson(string body) => Enqueue(HttpStatusCode.OK, body);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        Requests.Add((
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            body));

        var index = Math.Min(_callIndex, _script.Count - 1);
        _callIndex++;
        if (_script.Count == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(_script[index](request));
    }
}
