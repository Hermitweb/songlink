using System.Net.WebSockets;
using System.Text;
using SongLink.Host;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>契约 §3.6 import 本地音源库测试：LocalLibrary 纯逻辑 + Host POST /import → search 命中集成。</summary>
public class ImportTests
{
    [Fact]
    public void LocalLibrary_import_and_search()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sl-lib-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new LocalLibrary(dir);
            var t = lib.Import("我的歌", "张三", new byte[] { 1, 2, 3 });
            Assert.Equal("我的歌", t.Title);
            Assert.True(File.Exists(Path.Combine(dir, t.File)));   // 落盘
            Assert.Equal(1, lib.Count);

            Assert.Contains(lib.Search("我的"), x => x.Title == "我的歌");
            Assert.Contains(lib.Search("张三"), x => x.Artist == "张三");
            Assert.Empty(lib.Search("不存在"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LocalLibrary_rejects_empty_title()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sl-lib-" + Guid.NewGuid().ToString("N"));
        try { Assert.Throws<ArgumentException>(() => new LocalLibrary(dir).Import("", "x", null)); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task HostServer_import_then_search_finds_it()
    {
        int port = 52500 + Random.Shared.Next(300);
        const string Pin = "666666";
        await using var server = new HostServer(new FakeAdapter(), port, Pin);
        server.Start(port);
        await Task.Delay(200);

        // 1) HTTP 导入
        using (var http = new HttpClient())
        {
            var content = new ByteArrayContent(new byte[] { 9, 8, 7 });   // 假音频字节
            var resp = await http.PostAsync($"http://localhost:{port}/import?title={Uri.EscapeDataString("导入测试曲")}&artist={Uri.EscapeDataString("李四")}", content);
            Assert.True(resp.IsSuccessStatusCode, $"import 应 200，实际 {resp.StatusCode}");
        }

        // 2) WS 搜索命中
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        await Send(ws, new Hello { Role = "remote", Device = "t", Pin = Pin, Id = "h" });
        await ReadUntil(ws, f => f is AuthResult a && a.Ok, 2000);

        await Send(ws, new Cmd { Id = "s1", Action = "search", Args = new() { ["kw"] = "导入测试" } });
        var sr = await ReadUntil(ws, f => f is SearchResults r && r.Ref == "s1", 2000) as SearchResults;
        Assert.NotNull(sr);
        Assert.Contains(sr!.Results, x => x.Title == "导入测试曲");
    }

    private static async Task Send(WebSocket ws, Frame f)
    {
        var bytes = Encoding.UTF8.GetBytes(FrameCodec.Serialize(f));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<Frame?> ReadUntil(WebSocket ws, Func<Frame, bool> pred, int ms)
    {
        var buf = new byte[8192];
        var dl = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < dl)
        {
            using var cts = new CancellationTokenSource(dl - DateTime.UtcNow);
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            try { do { r = await ws.ReceiveAsync(buf, cts.Token); sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); } while (!r.EndOfMessage); }
            catch { return null; }
            var f = FrameCodec.Parse(sb.ToString());
            if (f is not null && pred(f)) return f;
        }
        return null;
    }
}
