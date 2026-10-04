using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using SongLink.Protocol;

namespace SongLink.RemoteSim;

/// <summary>
/// Remote 客户端模拟器（纯托管）：命令行扮演安卓遥控端，连 Host 收 state/ack、发指令。
/// 既是 M1 集成测试工具，也是无手机时的手动验证台。
///
/// 用法：
///   dotnet run -- --url ws://localhost:51999/ --cmds "next,pause,pick:晴天" --seconds 3
///   dotnet run -- --url ws://localhost:51999/            # 交互模式，逐行输入 next/pause/pick 晴天/quit
/// </summary>
internal static class Program
{
    private static long _seq;
    private static long Next() => Interlocked.Increment(ref _seq);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string url = GetArg(args, "--url") ?? "ws://localhost:51999/";
        var cmds = (GetArg(args, "--cmds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int seconds = int.TryParse(GetArg(args, "--seconds"), out var s) ? s : 0;

        using var ws = new ClientWebSocket();
        try { await ws.ConnectAsync(new Uri(url), CancellationToken.None); }
        catch (Exception ex) { Console.WriteLine($"[x] 连接失败 {url}：{ex.Message}"); return 2; }
        Console.WriteLine($"[remote-sim] 已连接 {url}");

        await Send(ws, new Hello { Role = "remote", Device = "remote-sim", Caps = new() { "cmd", "state", "lyric", "art" }, Pin = GetArg(args, "--pin"), Seq = Next() });

        var cts = new CancellationTokenSource();
        var reader = Task.Run(() => ReadLoop(ws, cts.Token));

        if (cmds.Length > 0)
        {
            foreach (var c in cmds) { await SendCmd(ws, c); await Task.Delay(300); }
            await Task.Delay(TimeSpan.FromSeconds(seconds > 0 ? seconds : 2));
            cts.Cancel();
        }
        else
        {
            Console.WriteLine("输入指令回车发送：");
            Console.WriteLine("  传输: next prev play pause stop toggle");
            Console.WriteLine("  点歌: pick <标题> [艺人]   搜索: search <关键词>");
            Console.WriteLine("  校准: calibrate <毫秒> [标题] [艺人]   模式: setSourceMode <PC_LOCAL|PHONE_STREAM>");
            Console.WriteLine("  退出: quit");
            string? line;
            while ((line = Console.ReadLine()) is not null && line.Trim() != "quit")
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                await SendCmd(ws, line.Trim());
            }
            cts.Cancel();
        }

        try { await reader; } catch { }
        if (ws.State == WebSocketState.Open) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        return 0;
    }

    private static async Task SendCmd(WebSocket ws, string spec)
    {
        // 支持 "pick 晴天 周杰伦" / "pick:晴天:周杰伦" / "search Coldplay Yellow" / "calibrate 250 晴天 周杰伦" / "next"
        var toks = spec.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (toks.Length == 0) return;
        var action = toks[0].ToLowerInvariant();
        var cmd = new Cmd { Id = Guid.NewGuid().ToString("N")[..8], Action = action, Seq = Next() };

        switch (action)
        {
            case "pick":
                if (toks.Length > 1)
                {
                    var o = new JsonObject { ["title"] = toks[1] };
                    if (toks.Length > 2) o["artist"] = toks[2];
                    cmd.Args = o;
                }
                break;
            case "search":
                if (toks.Length > 1) cmd.Args = new JsonObject { ["kw"] = string.Join(' ', toks.Skip(1)) };
                break;
            case "calibrate":
                if (toks.Length > 1 && int.TryParse(toks[1], out var ms))
                {
                    var o = new JsonObject { ["lyricOffsetMs"] = ms };
                    if (toks.Length > 2) o["title"] = toks[2];
                    if (toks.Length > 3) o["artist"] = toks[3];
                    cmd.Args = o;
                }
                break;
            case "setsourcemode":
                if (toks.Length > 1) cmd.Args = new JsonObject { ["mode"] = toks[1] };
                break;
        }

        await Send(ws, cmd);
        Console.WriteLine($"→ 发送 {action}{(toks.Length > 1 ? " " + string.Join(' ', toks.Skip(1)) : "")}");
    }

    private static async Task ReadLoop(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string text;
            try
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                text = sb.ToString();
            }
            catch { return; }

            var f = FrameCodec.Parse(text);
            switch (f)
            {
                case State st: Console.WriteLine($"  ← state: {st.Now.Title} — {st.Now.Artist}  [{st.Now.Status}] pos={st.Now.Pos}s"); break;
                case Ack a: Console.WriteLine($"  ← ack ref={a.Ref} ok={a.Ok}"); break;
                case ErrorFrame e: Console.WriteLine($"  ← error {e.Code} {e.Hint}"); break;
                case AuthResult ar: Console.WriteLine($"  ← authResult ok={ar.Ok}{(string.IsNullOrEmpty(ar.Token) ? "" : " token=" + ar.Token[..Math.Min(8, ar.Token.Length)] + "…")} {ar.Code}"); break;
                case SearchResults sr: Console.WriteLine($"  ← searchResults \"{sr.Query}\" → {sr.Results.Count} 条：{string.Join(", ", sr.Results.Take(5).Select(r => r.Title))}"); break;
                case Lyric ly: Console.WriteLine($"  ← lyric {ly.TrackId} ({ly.Lines.Count} 行)"); break;
                case Hello h: Console.WriteLine($"  ← hello host role={h.Role} dev={h.Device}"); break;
                default: Console.WriteLine($"  ← {f?.GetType().Name ?? "未知帧"}"); break;
            }
        }
    }

    private static async Task Send(WebSocket ws, Frame f)
    {
        var bytes = Encoding.UTF8.GetBytes(FrameCodec.Serialize(f));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1];
        return null;
    }
}
