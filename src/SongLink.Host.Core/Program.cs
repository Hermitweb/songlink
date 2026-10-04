using System.Net.WebSockets;
using System.Threading.Channels;
using System.Text;
using SongLink.Protocol;

namespace SongLink.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var mode = args.Length > 0 ? args[0] : "run";
        if (mode == "selftest") return await SelfTest();

        // 组合根：appsettings.json + 命令行覆盖 → 选适配器 → 起服务
        var cfg = HostConfig.LoadFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        if (args.Length > 1 && int.TryParse(args[1], out var pp)) cfg.Port = pp;   // 兼容 run 51999
        cfg.ApplyArgs(args);

        IPlayerAdapter adapter;
        try { adapter = AdapterFactory.Create(cfg); }
        catch (Exception ex) { Console.WriteLine($"[x] 适配器初始化失败：{ex.Message}"); return 2; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var server = new HostServer(adapter, cfg.Port, cfg.Pin);
        server.Start(cfg.Port);
        Console.WriteLine($"[host] 组合根：adapter={cfg.Adapter}  port={cfg.Port}  外部程序集={cfg.AdapterAssembly ?? "(内置)"}");
        Console.WriteLine("按 Ctrl+C 退出。");
        await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { });
        return 0;
    }

    /// <summary>本机端到端：起 Host → 连一个 Remote 客户端 → 走协议闭环并断言。</summary>
    private static async Task<int> SelfTest()
    {
        int port = 51999;
        Console.WriteLine($"[selftest] 启动 Host(ws://localhost:{port}) + FakeAdapter");
        const string Pin = "246810";
        await using var server = new HostServer(new FakeAdapter(), port, pin: Pin);
        server.Start(port);
        await Task.Delay(200);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        var rx = new WsReader(ws);
        Console.WriteLine("[selftest] 客户端已连接");

        // 0) 未鉴权先发指令 → 应被拒 AUTH_FAILED（#6 负例）
        await Send(ws, new Cmd { Id = "t0", Action = "next" });
        var denied = await rx.ReadUntil(f => f is ErrorFrame e && e.Code == "AUTH_FAILED");
        Check(denied is not null, "未鉴权指令 → error AUTH_FAILED");

        // 0b) 带正确 PIN 的 Hello → authResult ok + token（#6 正例）
        await Send(ws, new Hello { Role = "remote", Device = "test", Caps = new() { "cmd", "state" }, Pin = Pin, Id = "h1" });
        var auth = await rx.ReadUntil(f => f is AuthResult a && a.Ref == "h1") as AuthResult;
        Check(auth is { Ok: true } && !string.IsNullOrEmpty(auth.Token), "PIN 正确 → authResult ok + token");

        // 1) 鉴权后应收到 host 的 hello
        Check(await rx.ReadUntil(f => f is Hello) is not null, "收到 host hello");

        // 2) 发 next → ack ok=true
        await Send(ws, new Cmd { Id = "t1", Action = "next" });
        var ack = await rx.ReadUntil(f => f is Ack a && a.Ref == "t1") as Ack;
        Check(ack is { Ok: true }, "next → ack ok=true");

        // 3) 发 pause → ack，且随后 state.status=paused
        await Send(ws, new Cmd { Id = "t2", Action = "pause" });
        var ack2 = await rx.ReadUntil(f => f is Ack a && a.Ref == "t2") as Ack;
        Check(ack2 is { Ok: true }, "pause → ack ok=true");
        Check(await rx.ReadUntil(f => f is State s && s.Now.Status == "paused", 1500) is not null, "收到 state.status=paused");

        // 4) pick 已知曲 → ok
        await Send(ws, new Cmd { Id = "t3", Action = "pick", Args = NodeObj(("title", "Yellow"), ("artist", "Coldplay")) });
        var ack3 = await rx.ReadUntil(f => f is Ack a && a.Ref == "t3") as Ack;
        Check(ack3 is { Ok: true }, "pick 已知曲 → ack ok=true");

        // 5) 换曲后下发歌词帧
        var ly = await rx.ReadUntil(f => f is Lyric l && l.Lines.Count > 0, 1500) as Lyric;
        Check(ly is not null, $"收到 lyric 帧（{(ly?.Lines.Count ?? 0)} 行）");

        // 6) srcMeta → state.sourceMode=PHONE_STREAM
        await Send(ws, new SrcMeta { Title = "手机里的歌", Artist = "某歌手", Pos = 3, Dur = 200, Status = "playing" });
        Check(await rx.ReadUntil(f => f is State s && s.Now.SourceMode == "PHONE_STREAM", 1500) is not null, "srcMeta → state.sourceMode=PHONE_STREAM");

        // 7) 广播泄露回归：未鉴权第二条连接不得收到 state 广播
        using var ws2 = new ClientWebSocket();
        await ws2.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        var rx2 = new WsReader(ws2);
        await Send(ws2, new Hello { Role = "remote", Device = "spy", Pin = "000000", Id = "h2" });
        var badAuth = await rx2.ReadUntil(f => f is AuthResult a && a.Ref == "h2" && !a.Ok) as AuthResult;
        Check(badAuth is { Ok: false }, "错误 PIN → authResult ok=false");
        await Send(ws, new Cmd { Id = "t9", Action = "next" });   // 触发一次广播
        await Task.Delay(600);
        Check(await rx2.Next(200) is not State, "未鉴权连接未收到 state 广播（泄露已堵）");

        // 8) #4：/art 封面端点
        using (var http = new HttpClient())
        {
            var ok = await http.GetAsync($"http://localhost:{port}/art/demo-0");
            var body = ok.IsSuccessStatusCode ? await ok.Content.ReadAsByteArrayAsync() : Array.Empty<byte>();
            Check(ok.StatusCode == System.Net.HttpStatusCode.OK && body.Length > 0
                  && ok.Content.Headers.ContentType?.MediaType == "image/png",
                  $"GET /art/demo-0 → 200 image/png（{body.Length}B）");
            var miss = await http.GetAsync($"http://localhost:{port}/art/nope-999");
            Check(miss.StatusCode == System.Net.HttpStatusCode.NotFound, "GET /art/{未知} → 404");
        }

        // 9) #7：连发多条指令制造跨线程广播，收到的帧 seq 应严格单调递增
        for (int i = 0; i < 6; i++) await Send(ws, new Cmd { Id = $"o{i}", Action = "next" });
        long prevSeq = long.MinValue; bool monotonic = true; int seen = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            var f = await rx.Next(250);
            if (f is null) continue;
            if (f.Seq <= prevSeq) monotonic = false;
            prevSeq = f.Seq; seen++;
        }
        Check(monotonic && seen >= 6, $"#7 有序：收到 {seen} 帧，seq 严格单调递增={monotonic}");

        // 10) #5：搜索闭环 cmd:search → searchResults
        await Send(ws, new Cmd { Id = "s1", Action = "search", Args = NodeObj(("kw", "Yellow")) });
        var sr = await rx.ReadUntil(f => f is SearchResults r && r.Ref == "s1", 1500) as SearchResults;
        Check(sr is not null && sr.Results.Any(x => x.Title == "Yellow"), $"cmd:search → searchResults（{sr?.Results.Count ?? 0} 条命中）");

        // 11) #12：Source Mode 与 MediaBus 互斥整合——进 PHONE_STREAM 冻结 PC 出声
        await Send(ws, new Cmd { Id = "m1", Action = "setSourceMode", Args = NodeObj(("mode", "PC_LOCAL")) });
        Check(await rx.ReadUntil(f => f is EventFrame e && e.Name == "source-mode-changed" && e.Mode == "PC_LOCAL", 1500) is not null, "setSourceMode PC_LOCAL → event");
        await Send(ws, new Cmd { Id = "m2", Action = "play" });
        await rx.ReadUntil(f => f is State s && s.Now.SourceMode == "PC_LOCAL" && s.Now.Status == "playing", 1500);

        await Send(ws, new Cmd { Id = "m3", Action = "setSourceMode", Args = NodeObj(("mode", "PHONE_STREAM")) });
        Check(await rx.ReadUntil(f => f is EventFrame e && e.Name == "source-mode-changed" && e.Mode == "PHONE_STREAM", 1500) is not null, "setSourceMode PHONE_STREAM → event");
        await Send(ws, new SrcMeta { Title = "手机里的歌", Artist = "某歌手", Pos = 1, Dur = 200, Status = "playing" });
        var phoneState = await rx.ReadUntil(f => f is State s && s.Now.SourceMode == "PHONE_STREAM" && s.Now.Title == "手机里的歌", 1500) as State;
        Check(phoneState is not null, "srcMeta 驱动 PHONE_STREAM 真值");

        await Send(ws, new Cmd { Id = "m4", Action = "setSourceMode", Args = NodeObj(("mode", "PC_LOCAL")) });
        await rx.ReadUntil(f => f is EventFrame e && e.Name == "source-mode-changed" && e.Mode == "PC_LOCAL", 1500);
        var frozen = await rx.ReadUntil(f => f is State s && s.Now.SourceMode == "PC_LOCAL" && s.Now.Status == "paused", 1500) as State;
        Check(frozen is not null, "回 PC_LOCAL 显示 paused → 证明进 PHONE_STREAM 时已冻结 PC 出声（#12）");

        // 12) 契约 §7：lastSeq 断线补发
        long baseSeq;
        using (var wsC = new ClientWebSocket())
        {
            await wsC.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
            var rxC = new WsReader(wsC);
            await Send(wsC, new Hello { Role = "remote", Device = "cursor", Pin = Pin, Id = "hc" });
            var initSt = await rxC.ReadUntil(f => f is State, 1500) as State;
            baseSeq = initSt?.Seq ?? 0;
        } // wsC 关闭，之后的广播它收不到

        await Send(ws, new Cmd { Id = "r1", Action = "play" }); await Task.Delay(300);
        await Send(ws, new Cmd { Id = "r2", Action = "next" }); await Task.Delay(300);
        await Send(ws, new Cmd { Id = "r3", Action = "next" }); await Task.Delay(300);

        using (var wsR = new ClientWebSocket())
        {
            await wsR.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
            var rxR = new WsReader(wsR);
            await Send(wsR, new Hello { Role = "remote", Device = "rejoin", Pin = Pin, Id = "hr", LastSeq = baseSeq });
            int replayed = 0;
            var dl = DateTime.UtcNow.AddMilliseconds(1000);
            while (DateTime.UtcNow < dl)
            {
                var f = await rxR.Next(250);
                if (f is State st && st.Seq > baseSeq) replayed++;
            }
            Check(replayed >= 2, $"lastSeq={baseSeq} 断线补发：重连收到 {replayed} 条 seq>base 的 state");
        }

        // 13) #11：唇同步 offset —— calibrate 存偏移并立即重发带 offsetMs 的歌词
        await Send(ws, new Cmd { Id = "c1", Action = "pick", Args = NodeObj(("title", "Yellow"), ("artist", "Coldplay")) });
        await rx.ReadUntil(f => f is State s && s.Now.Title == "Yellow", 1500);
        await Send(ws, new Cmd { Id = "c2", Action = "calibrate", Args = CalibObj(300, "Yellow", "Coldplay") });
        var calAck = await rx.ReadUntil(f => f is Ack a && a.Ref == "c2", 1500) as Ack;
        Check(calAck is { Ok: true }, "calibrate → ack ok");
        var lyOff = await rx.ReadUntil(f => f is Lyric l && l.OffsetMs == 300, 1500) as Lyric;
        Check(lyOff is not null, "calibrate 立即重发 offsetMs=300 的 lyric（#11）");

        Console.WriteLine(_fail == 0 ? "\n[selftest] ✅ 全部通过：M1 局域网端到端协议闭环成立" : $"\n[selftest] ❌ 失败 {_fail} 项");
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        return _fail == 0 ? 0 : 1;
    }

    private static int _fail = 0;
    private static void Check(bool cond, string what)
    {
        Console.WriteLine($"  {(cond ? "✓" : "✗")} {what}");
        if (!cond) _fail++;
    }

    private static System.Text.Json.Nodes.JsonObject NodeObj(params (string k, string v)[] kv)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    private static System.Text.Json.Nodes.JsonObject CalibObj(int ms, string title, string artist)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        o["lyricOffsetMs"] = ms; o["title"] = title; o["artist"] = artist;
        return o;
    }

    private static async Task Send(WebSocket ws, Frame f)
    {
        var bytes = Encoding.UTF8.GetBytes(FrameCodec.Serialize(f));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>
    /// 后台持续收帧灌入 Channel；测试只从 Channel 读（取消 Channel 读安全，绝不取消 socket 接收，
    /// 避免 ClientWebSocket 被取消后置 Aborted 不可恢复）。
    /// </summary>
    private sealed class WsReader
    {
        private readonly WebSocket _ws;
        private readonly Channel<Frame> _rx = Channel.CreateUnbounded<Frame>();
        private readonly Task _loop;

        public WsReader(WebSocket ws) { _ws = ws; _loop = Task.Run(RunAsync); }

        private async Task RunAsync()
        {
            var buf = new byte[8192];
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    } while (!r.EndOfMessage);
                    var f = FrameCodec.Parse(sb.ToString());
                    if (f is not null) _rx.Writer.TryWrite(f);
                }
            }
            catch { /* socket 关闭/异常：结束读取 */ }
            finally { _rx.Writer.TryComplete(); }
        }

        public async Task<Frame?> ReadUntil(Func<Frame, bool> pred, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
                Frame f;
                try { f = await _rx.Reader.ReadAsync(cts.Token); }
                catch { return null; }   // 超时或通道关闭
                if (pred(f)) return f;
            }
            return null;
        }

        public async Task<Frame?> Next(int timeoutMs)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            try { return await _rx.Reader.ReadAsync(cts.Token); }
            catch { return null; }
        }
    }
}
