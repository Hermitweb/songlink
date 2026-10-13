using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SongLink.Protocol;

namespace SongLink.Host.Tests.Integration;

/// <summary>
/// 集成测试用的最小 SongLink 服务器（测试工程原来直接引用了它，但代码库里并不存在）。
///
/// 只实现被测流程需要的两条应答：
///   Hello → authResult（首次配对下发 token；带 token 重连校验 token）
///   Cmd   → ack（回同一个 ref）
/// 传输层用 HttpListener 自带的 WebSocket 支持，不引入额外 NuGet 包；
/// HttpListener 只监听 localhost，无需 URL ACL 授权。
/// </summary>
internal sealed class FakeHostServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>
    /// 已签发的 token 集合。必须是**服务端级**而非连接级：
    /// 断线重连会建立全新 WebSocket 连接，若按连接存 token，重连永远校验失败。
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _tokens = new();

    public FakeHostServer(int port) => _listener.Prefixes.Add($"http://localhost:{port}/");

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }   // listener 已 Stop/Close

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(ctx, ct), ct);
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocketSession? session = null;
        try
        {
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            session = new WebSocketSession(ws);

            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // WebSocket 消息可能被拆成多帧，必须读到 EndOfMessage 再按整帧解析
                var sb = new StringBuilder();
                WebSocketReceiveResult received;
                do
                {
                    received = await ws.ReceiveAsync(buffer, ct);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                } while (!received.EndOfMessage);

                var reply = Handle(FrameCodec.Parse(sb.ToString()));
                if (reply is null) continue;

                var payload = Encoding.UTF8.GetBytes(FrameCodec.Serialize(reply));
                await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception)
        {
            // 客户端断开 / 服务器关闭，集成测试场景下无需向上抛
        }
        finally
        {
            session?.Dispose();
        }
    }

    private Frame? Handle(Frame? frame)
    {
        switch (frame)
        {
            case Hello hello:
            {
                // 带 token 即为重连：token 必须是本服务端此前签发过的；
                // 不带 token 为首次配对：签发新 token
                var reconnect = !string.IsNullOrEmpty(hello.Token);
                var ok = !reconnect || _tokens.ContainsKey(hello.Token!);

                string? token = null;
                if (ok)
                {
                    token = hello.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                    _tokens[token] = hello.Id ?? "";
                }

                return new AuthResult
                {
                    Ok = ok,
                    Token = token,
                    Ref = hello.Id ?? "",
                    Code = ok ? null : "AUTH_FAILED"
                };
            }

            case Cmd cmd:
                return new Ack { Ok = true, Ref = cmd.Id ?? "" };

            default:
                return null;   // state/ping 等帧本工具不处理
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* 已停止 */ }
        _listener.Close();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* 取消导致 */ }
        }

        _cts.Dispose();
    }

    private sealed class WebSocketSession : IDisposable
    {
        private readonly WebSocket _ws;

        public WebSocketSession(WebSocket ws) => _ws = ws;

        public void Dispose()
        {
            try { if (_ws.State == WebSocketState.Open) _ws.Abort(); } catch { /* ignore */ }
            _ws.Dispose();
        }
    }
}