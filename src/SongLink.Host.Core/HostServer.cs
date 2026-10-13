using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Net;
using System.Text;
using SongLink.Protocol;
using SongLink.Host.AudioStream;
using System.Net.Sockets; // For UDP port validation if needed

namespace SongLink.Host;

/// <summary>
/// M1 Host 中枢（架构 §6.3/§6.6）：HttpListener WebSocket 服务器 + 帧分发。
/// 每连接一个有序出箱 Channel + 单一发送循环（#7 保证 FIFO、且永不并发 SendAsync）；
/// 广播前 Clone NowPlaying（#8 不改共享实例）。纯托管、可在本机跑（FakeAdapter 时无需 WinRT/声卡）。
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly MediaBus _bus;
    private readonly IPlayerAdapter _adapter;
    private readonly LyricService _lyric = new();
    private readonly ArtStore _art = new();
    private readonly PlayQueue _queue = new();
    private readonly LocalLibrary _library = new();
    private readonly AuthManager _auth;
    private readonly string _hostKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private readonly ReplayBuffer _replay = new();
    private readonly object _pubLock = new();
    private readonly IAudioRenderer? _audioRenderer;
    private readonly bool _requireFrameSig;
    private AudioStreamSink? _audioSink;
    private readonly ConcurrentDictionary<int, ClientConn> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private long _seq;
    private string _lastTrackKey = "";

    public string Pin => _auth.Pin;
    public string HostKey => _hostKey;
    public AudioStreamSink? AudioSink => _audioSink;

    private sealed class ClientConn
    {
        public required WebSocket Socket { get; init; }
        public required Channel<Frame> Outbox { get; init; }
        public Task? SendTask { get; set; }
        public List<string> Caps { get; } = new();
        public bool Authed { get; set; }
        public string? Token { get; set; }
    }

    public HostServer(IPlayerAdapter adapter, int port, string? pin = null, IAudioRenderer? audioRenderer = null, bool requireFrameSig = false)
    {
        _adapter = adapter;
        _auth = new AuthManager(pin);
        _audioRenderer = audioRenderer;
        _requireFrameSig = requireFrameSig;
        _bus = new MediaBus(adapter);
        _bus.Changed += OnStateChanged;
    }

    public int Port { get; private set; }

    public void Start(int port)
    {
        Port = port;
        // #1：优先绑定通配(手机等局域网设备可连)；无权限则回落 localhost。
        bool lan = false;
        try
        {
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
            lan = true;
        }
        catch (HttpListenerException)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }
        _bus.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        if (lan)
            Console.WriteLine($"[host] 监听 ws://0.0.0.0:{port}/（局域网可连）  PIN={_auth.Pin}  适配器={_adapter.AppId} caps={_adapter.Caps}");
        else
            Console.WriteLine($"[warn] 仅绑定 localhost（手机连不上）。放行局域网：以管理员运行，或执行 " +
                              $"netsh http add urlacl url=http://+:{port}/ user={{登录用户}}  后重启。  PIN={_auth.Pin}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (_cts.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ServeHttp(ctx);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(ctx));
        }
    }

    /// <summary>GET / 桌面 Web 面板；GET /static/* 静态资源；GET /art/{ref} 封面；POST /import 导入音源；/docs Swagger UI。</summary>
    private void ServeHttp(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        try
        {
            // 0. 桌面 Web 面板 (默认首页 + 静态资源)
            if (path == "/" || path == "/index.html")
            {
                ServeStaticFile(ctx, "wwwroot/index.html", "text/html; charset=utf-8");
            }
            else if (path.StartsWith("/static/", StringComparison.Ordinal))
            {
                var rel = path["/static/".Length..];
                // 路径穿越防护 — 不允许 ..
                if (rel.Contains("..", StringComparison.Ordinal)) { ctx.Response.StatusCode = 400; return; }
                ServeStaticFile(ctx, "wwwroot/" + rel, MimeFromExt(rel));
            }
            // 1. Swagger UI 文档
            else if (path == "/docs")
            {
                var htmlContent = @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='utf-8' />
    <meta name='viewport' content='width=device-width, initial-scale=1' />
    <title>SongLink API 文档</title>
    <link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/4.15.5/swagger-ui.min.css' />
    <style>body { margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }</style>
</head>
<body>
    <div id='swagger-ui'></div>
    <script src='https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/4.15.5/swagger-ui-bundle.min.js'></script>
    <script src='https://cdnjs.cloudflare.com/ajax/libs/swagger-ui/4.15.5/swagger-ui-standalone-preset.min.js'></script>
    <script>
        window.onload = function() {
            SwaggerUIBundle({
                url: '/api-spec.json',
                dom_id: '#swagger-ui',
                presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
                layout: 'StandaloneLayout',
                deepLinking: true,
                showExtensions: true,
                showCommonExtensions: true
            });
        };
    </script>
</body>
</html>";
                
                var bytes = Encoding.UTF8.GetBytes(htmlContent);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            
            // 2. OpenAPI 3.0 规范（JSON）
            else if (path == "/api-spec.json")
            {
                var yamlContent = @"openapi: 3.0.3
info:
  title: SongLink Protocol API
  version: 1.0.0
servers:
  - url: ws://localhost:51999
paths:
  /ws:
    get:
      summary: WebSocket 连接入口
components:
  schemas:
    Hello:
      type: object
      properties:
        v: { type: integer, enum: [1] }
        type: { type: string, enum: [hello] }
        role: { type: string, enum: [remote, host] }
        pin: { type: string, pattern: '^\d{6}$' }
        token: { type: string }
    Cmd:
      type: object
      properties:
        type: { type: string, enum: [cmd] }
        action: { type: string, enum: [play, pause, stop, next, prev, pick, search, queue.add, queue.top, queue.remove, queue.clear, queue.shuffle, queue.unshuffle, queue.move, setSourceMode, calibrate, getLyric, trending, history.list, volume] }
        args: { type: object }";
                
                var bytes = Encoding.UTF8.GetBytes(yamlContent);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            
            // 3. 专辑封面
            else if (ctx.Request.HttpMethod == "GET" && path.StartsWith("/art/", StringComparison.Ordinal))
            {
                var refId = Uri.UnescapeDataString(path["/art/".Length..]);
                var got = _art.Get(refId);
                if (got is not null)
                {
                    var bytes = got.Value.Bytes;
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = got.Value.Mime;
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else ctx.Response.StatusCode = 404;
            }
            
            // 4. 音源导入
            else if (ctx.Request.HttpMethod == "POST" && path == "/import")
            {
                byte[] body;
                using (var ms = new MemoryStream()) { ctx.Request.InputStream.CopyTo(ms); body = ms.ToArray(); }
                var title = ctx.Request.QueryString["title"] ?? "";
                var artist = ctx.Request.QueryString["artist"] ?? "";
                string json; int code;
                try
                {
                    var t = _library.Import(title, artist, body.Length > 0 ? body : null);
                    json = System.Text.Json.JsonSerializer.Serialize(new { ok = true, title = t.Title, artist = t.Artist, file = t.File });
                    code = 200;
                }
                catch (Exception ex) { json = System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = ex.Message }); code = 400; }
                var b = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
            }
            
            // 默认 404
            else ctx.Response.StatusCode = 404;
        }
        finally { ctx.Response.Close(); }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx)
    {
        WebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
        catch (Exception ex) { Console.WriteLine($"[host] 握手失败：{ex.Message}"); return; }

        var conn = new ClientConn
        {
            Socket = wsCtx.WebSocket,
            Outbox = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false })
        };
        int id = conn.Socket.GetHashCode();
        _clients[id] = conn;
        conn.SendTask = Task.Run(() => SendLoopAsync(conn));   // #7：单发送循环，FIFO
        Console.WriteLine($"[host] 客户端接入 #{id}（在线 {_clients.Count}）");

        try
        {
            // 等待客户端先发 Hello 完成鉴权；鉴权成功后再回 host hello + 当前 state（见 DispatchAsync）
            var buf = new byte[8192];
            while (conn.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await conn.Socket.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;

                var frame = FrameCodec.Parse(sb.ToString());
                if (frame is null) { SendTo(conn, new ErrorFrame { Code = "BAD_PROTO" }); continue; }
                await DispatchAsync(conn, frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(id, out _);
            conn.Outbox.Writer.TryComplete();
            try { if (conn.SendTask is not null) await conn.SendTask; } catch { }
            Console.WriteLine($"[host] 客户端断开 #{id}（在线 {_clients.Count}）");
        }
    }

    private async Task DispatchAsync(ClientConn conn, Frame frame)
    {
        // #6 鉴权门：未鉴权只允许 Hello
        if (!conn.Authed && frame is not Hello)
        {
            SendTo(conn, new ErrorFrame { Ref = frame.Id, Code = "AUTH_FAILED" });
            return;
        }

        // 契约 §6：入站命令帧强制 HMAC 验签（用该连接配对后的 token 作密钥）
        if (_requireFrameSig && conn.Authed && frame is Cmd &&
            (conn.Token is null || !FrameSigner.Verify(conn.Token, frame)))
        {
            SendTo(conn, new ErrorFrame { Ref = frame.Id, Code = "AUTH_FAILED", Hint = "missing/invalid sig" });
            return;
        }

        switch (frame)
        {
            case Hello h:
                bool authOk = _auth.ValidatePin(h.Pin) || _auth.ValidateToken(h.Token);
                if (authOk)
                {
                    conn.Authed = true;
                    conn.Caps.Clear(); conn.Caps.AddRange(h.Caps);
                    conn.Token = _auth.IssueToken(h.Device ?? "unknown");
                    SendTo(conn, new AuthResult { Ref = h.Id ?? "", Ok = true, Token = conn.Token, HostKey = _requireFrameSig ? _hostKey : null });
                    SendTo(conn, new Hello { Role = "host", Device = Environment.MachineName, Os = "Windows" });
                    if (h.LastSeq is > 0) ReplaySince(conn, h.LastSeq.Value);   // §7 断线补发
                    else if (_bus.Current is not null) SendTo(conn, new State { Now = _bus.Current.Clone() });
                }
                else
                {
                    SendTo(conn, new AuthResult { Ref = h.Id ?? "", Ok = false, Code = "AUTH_FAILED" });
                }
                break;

            case Ping p:
                if (_bus.Current is not null) SendTo(conn, new State { Now = _bus.Current.Clone() });
                break;

            case SrcMeta sm:
                _bus.SetPhoneNowPlaying(new NowPlaying
                {
                    Title = sm.Title, Artist = sm.Artist, Pos = sm.Pos, Dur = sm.Dur,
                    Status = sm.Status, ArtRef = sm.ArtRef
                });
                break;

            case AudioStart a:
                // 创建 OpusCodec 并初始化 AudioStreamSink
                var opusCodec = new OpusCodec(
                    sampleRate: a.Rate > 0 ? a.Rate : 48000,
                    channels: a.Ch > 0 ? a.Ch : 2,
                    bitrateBps: a.FrameMs > 0 ? 128_000 : 64_000,
                    frameMs: a.FrameMs > 0 ? a.FrameMs : OpusCodec.DefaultFrameMs);

                // 把 audio:start 协商的端口作为 Host 监听端口（Android 端把这个值作为目标发送端口，
                // 默认 51000，见 protocol §3.9）。fallback 到 51000 保证与 Android 默认对齐。
                var hostPort = a.Port > 0 ? a.Port : 51000;
                _audioSink ??= new AudioStreamSink(opusCodec, _audioRenderer ?? new NullRenderer(), hostPort);

                // 真正把 UDP 监听器启起来 (arch §3.9 audio:start 真链路)
                // — 之前 commit 31a05e3 在这里只 Console.WriteLine 了端口,实际 UdpClient 没绑,
                //   Android 推的 PCM 包全部丢失。这是一个修复。
                try
                {
                    _audioSink.Start();
                    var port = _audioSink.LocalPort;
                    Console.WriteLine($"[audio] AudioStreamSink 启动,UDP listening on 0.0.0.0:{port}/ (audio:start.port={a.Port})");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[audio] 启动 AudioStreamSink 失败:{ex.Message}");
                    // 释放失败的 sink,让下一次 audio:start 能用新端口重试(否则 ??= 短路一直复用坏实例)。
                    try { _audioSink.Dispose(); } catch { /* 忽略 */ }
                    _audioSink = null;
                    SendTo(conn, new Ack { Ref = a.Id ?? "", Ok = false });
                    break;
                }

                SendTo(conn, new Ack { Ref = a.Id ?? "", Ok = true });
                break;

            case AudioStop:
                _audioSink?.Stop();    // 停渲染器,但保留 UDP 句柄以便重连 (audio:start 重新 Start)
                _audioSink?.Dispose(); // 完整释放 — audio 业务一般一次生命周期
                _audioSink = null;
                SendTo(conn, new Ack { Ref = "", Ok = true });
                break;

            case Cmd c when c.Action == "setSourceMode":
                var mode = c.Args?["mode"]?.GetValue<string>() ?? "PC_LOCAL";
                _bus.SetMode(mode);   // #12：进 PHONE_STREAM 时 MediaBus 冻结 PC 出声
                if (mode == "PC_LOCAL") _audioSink?.Stop();   // 交还输出给 PC
                SendTo(conn, new Ack { Ref = c.Id ?? "", Ok = true });
                Broadcast(new EventFrame { Name = "source-mode-changed", Mode = _bus.SourceMode });
                break;

            case Cmd c when c.Action.StartsWith("queue."):
                bool qOk = HandleQueue(c);
                SendTo(conn, new Ack { Ref = c.Id ?? "", Ok = qOk });
                if (!qOk && c.Action == "queue.add")
                    SendTo(conn, new ErrorFrame { Ref = c.Id, Code = "DUPLICATE", Hint = "已在队列中" });
                if (qOk) Broadcast(BuildQueueEvent());
                break;

            case Cmd c when c.Action == "search":
                var kw = SafeStr(c.Args?["kw"]) ?? "";
                var results = new List<SearchResultItem>();
                if ((_adapter.Caps & AdapterCapability.Search) != 0)
                    results.AddRange(await _adapter.Search(kw, _cts.Token));
                foreach (var t in _library.Search(kw))
                    if (!results.Any(r => r.Title == t.Title && r.Artist == t.Artist))
                        results.Add(new SearchResultItem { Title = t.Title, Artist = t.Artist });
                SendTo(conn, new SearchResults { Ref = c.Id ?? "", Query = kw, Results = results });
                break;

            case Cmd c when c.Action == "trending":
                HandleTrending(c, conn);
                break;

            case Cmd c when c.Action == "calibrate":
                int offMs = 0;
                try { offMs = c.Args?["lyricOffsetMs"]?.GetValue<int>() ?? 0; } catch { offMs = 0; }
                var cTitle = SafeStr(c.Args?["title"]);
                var cArtist = SafeStr(c.Args?["artist"]);
                _lyric.SetOffset(cTitle, cArtist, offMs);   // #11：title 空 → 全局偏移
                SendTo(conn, new Ack { Ref = c.Id ?? "", Ok = true });
                if (_bus.Current is not null)
                {
                    var ly = _lyric.TryGet(_bus.Current.Title, _bus.Current.Artist);
                    if (ly is not null) Broadcast(ly);       // 用新偏移立即重发，客户端即时见效
                }
                break;

            case Cmd c when c.Action == "getLyric":
                // 桌面 Web 面板用 — Android 走 LocalLyric 服务直接拿,这里给 Web 兜底。
                var gTitle = SafeStr(c.Args?["title"]) ?? "";
                var gArtist = SafeStr(c.Args?["artist"]) ?? "";
                var lyric = _lyric.TryGet(gTitle, gArtist);
                SendTo(conn, new Ack { Ref = c.Id ?? "", Ok = true });
                if (lyric is not null) SendTo(conn, lyric);
                break;

            case Cmd c when c.Action == "history.list":
                // Web 面板「历史回放」用 — 返回最近 N 条 PlayHistory,默认 20,上限 50。
                int hn = SafeInt(c.Args?["limit"]) ?? 20;
                if (hn < 1) hn = 20;
                if (hn > 50) hn = 50;
                var hist = _library.GetPlayHistory(hn);
                var arr = new System.Text.Json.Nodes.JsonArray();
                foreach (var h in hist)
                {
                    arr.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["title"] = h.Title,
                        ["artist"] = h.Artist,
                        ["playedAt"] = h.PlayedAt.ToString("o")   // ISO-8601
                    });
                }
                SendTo(conn, new Ack { Ref = c.Id ?? "", Ok = true });
                SendTo(conn, new EventFrame { Name = "history-list", Queue = arr });
                break;

            case Cmd c:
                bool ok = c.Action switch
                {
                    "play" => await _adapter.Play(_cts.Token),
                    "pause" => await _adapter.Pause(_cts.Token),
                    "toggle" => (_bus.Current?.Status == "playing") ? await _adapter.Pause(_cts.Token) : await _adapter.Play(_cts.Token),
                    "stop" => await _adapter.Stop(_cts.Token),
                    "next" => await _adapter.Next(_cts.Token),
                    "prev" => await _adapter.Prev(_cts.Token),
                    "pick" => (_adapter.Caps & AdapterCapability.Pick) != 0
                                && await _adapter.Pick(c.Args?["title"]?.GetValue<string>() ?? "", c.Args?["artist"]?.GetValue<string>() ?? "", _cts.Token),
                    "volume" => HandleVolume(c),
                    _ => false
                };
                SendTo(conn, new Ack { Ref = c.Id ?? "", Ok = ok });
                if (!ok && c.Action == "pick")
                    SendTo(conn, new ErrorFrame { Ref = c.Id, Code = (_adapter.Caps & AdapterCapability.Pick) == 0 ? "PICK_UNSUPPORTED" : "APP_NOT_FOUND" });
                // pick 成功 → 自动入播放历史(去重由 LocalLibrary.PlayHistory 倒序 + 50 上限保证)
                if (ok && c.Action == "pick")
                {
                    var t = c.Args?["title"]?.GetValue<string>() ?? "";
                    var ar = c.Args?["artist"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(t)) _library.AddPlayHistory(t, ar);
                }
                break;
        }
    }

    private void OnStateChanged(NowPlaying np)
    {
        // np 已是 MediaBus 产出的快照（含正确 SourceMode）
        BroadcastState(np);

        // 换曲才下发歌词（省流量，蓝牙也能带文本歌词）
        var key = $"{np.Title}|{np.Artist}";
        if (key != _lastTrackKey && !string.IsNullOrWhiteSpace(np.Title))
        {
            _lastTrackKey = key;
            var ly = _lyric.TryGet(np.Title, np.Artist);
            if (ly is not null) Broadcast(ly);
        }
    }

    private void BroadcastState(NowPlaying np)
    {
        lock (_pubLock)
        {
            var f = new State { Now = np.Clone(), Seq = Next() };
            SignOutbound(f);
            _replay.Add(f);
            foreach (var c in _clients.Values.Where(c => c.Authed)) c.Outbox.Writer.TryWrite(f);
        }
    }

    private void Broadcast(Frame frame)
    {
        lock (_pubLock)
        {
            frame.Seq = Next();
            SignOutbound(frame);
            _replay.Add(frame);
            foreach (var c in _clients.Values.Where(c => c.Authed)) c.Outbox.Writer.TryWrite(frame);
        }
    }

    /// <summary>点对点帧（ack/error/authResult/searchResults/hello）：锁内赋 seq，不入重放缓冲。</summary>
    private void SendTo(ClientConn conn, Frame frame)
    {
        lock (_pubLock) { frame.Seq = Next(); conn.Outbox.Writer.TryWrite(frame); }
    }

    /// <summary>出站广播帧签名（任务2）：仅签名模式开启时，用 host-key 盖 Sig（共享帧一次签名对所有连接/重放有效）。</summary>
    private void SignOutbound(Frame frame)
    {
        if (_requireFrameSig) frame.Sig = FrameSigner.Compute(_hostKey, frame);
    }

    /// <summary>§7 断线补发：把 seq &gt; lastSeq 的缓冲广播按原 seq 补发给该连接。</summary>
    private void ReplaySince(ClientConn conn, long lastSeq)
    {
        lock (_pubLock)
            foreach (var f in _replay.Since(lastSeq)) conn.Outbox.Writer.TryWrite(f);
    }

    private long Next() => Interlocked.Increment(ref _seq);

    private static string? SafeStr(System.Text.Json.Nodes.JsonNode? node)
    {
        try { return node?.GetValue<string>(); } catch { return null; }
    }

    private static int? SafeInt(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<int>(); }
        catch { return int.TryParse(node.ToString(), out var v) ? v : null; }
    }

    /// <summary>处理 volume 命令：args.level = 0.0~1.0（必填）。
    /// 成功时：更新 MediaBus 状态→广播 State；调 audioSink.SetVolume（PHONE_STREAM 时控制 PC 渲染器）；
    /// 调 adapter.SetVolume（Caps 含 Volume 时转发给源 App 自身音量）。</summary>
    private bool HandleVolume(Cmd c)
    {
        double v = 1.0;
        try
        {
            if (c.Args?["level"] is { } node) v = node.GetValue<double>();
            else if (c.Args?["volume"] is { } node2) v = node2.GetValue<double>();
            else return false;
        }
        catch { return false; }

        v = Math.Clamp(v, 0.0, 1.0);
        _bus.SetVolume(v);
        // PHONE_STREAM 模式下 sink 才是出声路径 → 直接推给渲染器
        _audioSink?.SetVolume(v);
        // 源 App 自身音量（NAudio WaveOutEvent 等），cap 检查避免空调
        if ((_adapter.Caps & AdapterCapability.Volume) != 0)
        {
            try { _ = _adapter.SetVolume(v, _cts.Token); } catch { /* 静默失败 */ }
        }
        Broadcast(new EventFrame { Name = "volume-changed" });
        return true;
    }

    /// <summary>处理 queue.* 命令（契约 §3.6 + 落雪风格增强）。返回是否成功。</summary>
    private bool HandleQueue(Cmd c)
    {
        switch (c.Action)
        {
            case "queue.add":
                {
                    var title = SafeStr(c.Args?["title"]) ?? "";
                    var artist = SafeStr(c.Args?["artist"]) ?? "";
                    var by = SafeStr(c.Args?["by"]) ?? "remote";
                    // 禁止重复:同 (title, artist) 已在队列里就跳过,Web 面板会显示 toast
                    if (_queue.Snapshot().Any(x => x.Title == title && x.Artist == artist))
                        return false;
                    _queue.Add(title, artist, by);
                    return true;
                }
            case "queue.remove":
                var ri = SafeInt(c.Args?["index"]); return ri is not null && _queue.RemoveAt(ri.Value);
            case "queue.top":
                var ti = SafeInt(c.Args?["index"]); return ti is not null && _queue.Top(ti.Value);
            case "queue.clear":
                _queue.Clear(); return true;
            case "queue.shuffle":
                _queue.Shuffle(); return true;
            case "queue.unshuffle":
                _queue.Unshuffle(); return true;
            case "queue.move":
                // 拖拽排序:把 index 从移到 toIndex(支持向上/向下移动)
                var from = SafeInt(c.Args?["from"]);
                var to = SafeInt(c.Args?["to"]);
                return from is not null && to is not null && MoveQueueItem(from.Value, to.Value);
            default:
                return false;
        }
    }

    /// <summary>把队列里 fromIndex 移到 toIndex 位置。toIndex 会被夹到 [0, Count-1]。</summary>
    private bool MoveQueueItem(int fromIndex, int toIndex)
    {
        return _queue.Move(fromIndex, toIndex);
    }

    /// <summary>流行/热门曲库（兜底,无网络依赖,纯静态预设）。Web 面板「热门」区用。</summary>
    private static readonly (string Title, string Artist)[] Trending =
    {
        ("晴天", "周杰伦"), ("稻香", "周杰伦"), ("七里香", "周杰伦"),
        ("Yellow", "Coldplay"), ("Shape of You", "Ed Sheeran"),
        ("演员", "薛之谦"), ("告白气球", "周杰伦"), ("青花瓷", "周杰伦"),
        ("后来", "刘若英"), ("那些年", "胡夏"), ("分手快乐", "梁静茹"),
        ("一生所爱", "卢冠廷"), ("海阔天空", "Beyond"), ("真的爱你", "Beyond"),
        ("光辉岁月", "Beyond"), ("千千阙歌", "陈慧娴"), ("吻别", "张学友"),
        ("十年", "陈奕迅"), ("浮夸", "陈奕迅"), ("红玫瑰", "陈奕迅"),
        ("Bad Habit", "Steve Lacy"), ("Anti-Hero", "Taylor Swift"),
        ("孤勇者", "陈奕迅"), ("起风了", "买辣椒也用券"), ("踏山河", "是七叔呢"),
    };

    /// <summary>处理 trending 命令 — 返回 N 条热门(默认 20, 上限 50)。</summary>
    private void HandleTrending(Cmd c, ClientConn conn)
    {
        int n = SafeInt(c.Args?["limit"]) ?? 20;
        if (n < 1) n = 20;
        if (n > 50) n = 50;
        var items = Trending.Take(n)
            .Select(t => new SearchResultItem { Title = t.Title, Artist = t.Artist })
            .ToList();
        SendTo(conn, new SearchResults { Ref = c.Id ?? "", Query = "trending", Results = items });
    }

    private EventFrame BuildQueueEvent()
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var it in _queue.Snapshot())
            arr.Add(new System.Text.Json.Nodes.JsonObject { ["title"] = it.Title, ["artist"] = it.Artist, ["by"] = it.By });
        return new EventFrame { Name = "queue-updated", Queue = arr };
    }

    /// <summary>每连接唯一的发送循环：串行、按序把出箱帧写到 socket（seq 已在入队时于锁内赋好）。</summary>
    private async Task SendLoopAsync(ClientConn conn)
    {
        try
        {
            await foreach (var frame in conn.Outbox.Reader.ReadAllAsync(_cts.Token))
            {
                if (conn.Socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(FrameCodec.Serialize(frame));
                await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch { /* 发送异常:断开由接收循环清理 */ }
    }

    /// <summary>从 AppContext.BaseDirectory 读文件并下发,文件缺失 → 404。</summary>
    private void ServeStaticFile(HttpListenerContext ctx, string relPath, string mime)
    {
        var full = Path.Combine(AppContext.BaseDirectory, relPath);
        if (!File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
        var bytes = File.ReadAllBytes(full);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static string MimeFromExt(string rel)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css"            => "text/css; charset=utf-8",
            ".js"             => "application/javascript; charset=utf-8",
            ".json"           => "application/json; charset=utf-8",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg"            => "image/svg+xml",
            ".ico"            => "image/x-icon",
            ".txt"            => "text/plain; charset=utf-8",
            _                 => "application/octet-stream"
        };
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var conn in _clients.Values) conn.Outbox.Writer.TryComplete();
        foreach (var conn in _clients.Values)
        {
            // CloseOutputAsync + 超时：只发出 close，不等对端回应，避免 testhost 挂起
            try
            {
                if (conn.Socket.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
                }
            }
            catch { /* 超时/已断，忽略 */ }
        }
        try { _listener.Stop(); } catch (ObjectDisposedException) { /* 已 dispose */ }
        try { _listener.Close(); } catch (ObjectDisposedException) { /* ignore */ }
        if (_acceptLoop is not null) { try { await _acceptLoop; } catch { } }
        _audioSink?.Dispose();
        await _bus.DisposeAsync();
    }
}
