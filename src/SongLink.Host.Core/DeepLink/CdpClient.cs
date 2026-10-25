using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SongLink.Host.DeepLink;

/// <summary>
/// 极简 Chrome DevTools Protocol 客户端：连到本机某个调试端口，
/// 找到目标页面，在页面里执行一段 JS 并取回结果。
///
/// <para>只依赖 .NET 自带的 <see cref="ClientWebSocket"/>，不引第三方包。</para>
/// </summary>
public sealed class CdpClient : IDisposable
{
    private const int ReceiveChunk = 64 * 1024;

    private readonly int _port;
    // **必须禁用代理**：HttpClient 默认读系统代理设置，本机配了一个 127.0.0.1:7890 的代理，
    // 访问 127.0.0.1 的 CDP 端口会被丢给代理然后失败（表现为"连不上"）。
    // 同理，任何要直连本机或国内音乐接口的请求都不该走代理。
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };
    private int _seq;

    public CdpClient(int port) => _port = port;

    /// <summary>调试端口上是否有可用的 page 目标。</summary>
    public static bool HasPageTarget(int port)
    {
        try
        {
            var c = new CdpClient(port);
            var t = c.FindPageTarget();
            c.Dispose();
            return t is not null;
        }
        catch { return false; }
    }

    /// <summary>找到第一个 url 以 <paramref name="urlPrefix"/> 开头的 page 目标。</summary>
    public (string WebSocketUrl, string Title, string Url)? FindPageTarget(string urlPrefix = "orpheus://")
    {
        try
        {
            using var resp = _http.GetAsync($"http://127.0.0.1:{_port}/json/list").GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (!t.TryGetProperty("type", out var ty) || ty.GetString() != "page") continue;
                var url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!url.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var ws = t.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
                if (string.IsNullOrEmpty(ws)) continue;
                var title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                return (ws, title, url);
            }
        }
        catch { }
        return null;
    }

    /// <summary>在目标页面里执行 JS，返回其值（对象会被序列化成 JSON 字符串）。</summary>
    public async Task<string?> EvaluateAsync(string expression, bool awaitPromise = false, CancellationToken ct = default)
    {
        var target = FindPageTarget();
        if (target is null) return null;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(target.Value.WebSocketUrl), ct).ConfigureAwait(false);

        var id = ++_seq;
        var payload = JsonSerializer.Serialize(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new
            {
                expression,
                returnByValue = true,
                awaitPromise,
                userGesture = true,
            },
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var buffer = new byte[ReceiveChunk];
        var msg = new StringBuilder();
        while (true)
        {
            var chunk = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (chunk.MessageType == WebSocketMessageType.Close) return null;
            msg.Append(Encoding.UTF8.GetString(buffer, 0, chunk.Count));
            if (!chunk.EndOfMessage) continue;

            // 一条 WebSocket 消息可能后面还跟着别的消息（事件、回应…），
            // 取完一条必须把缓冲清空，否则下一条会粘在后面导致 JSON 解析失败。
            var text = msg.ToString();
            msg.Clear();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException je)
            {
                throw new InvalidOperationException(
                    "CDP 返回的不是合法 JSON: " + je.Message + " | 原文前200字符: " + Cut(text), je);
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id) continue;

                var res = root.GetProperty("result");
                if (res.TryGetProperty("exceptionDetails", out var ex))
                {
                    var desc = ex.TryGetProperty("exception", out var e2) && e2.TryGetProperty("description", out var d)
                        ? d.GetString() : ex.TryGetProperty("text", out var t) ? t.GetString() : "JS 异常";
                    throw new InvalidOperationException("页面内 JS 报错: " + desc);
                }
                if (res.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                    return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
                return null;
            }
        }
    }

    private static string Cut(string s) => s.Length <= 200 ? s : s[..200];

    public void Dispose() => _http.Dispose();
}
