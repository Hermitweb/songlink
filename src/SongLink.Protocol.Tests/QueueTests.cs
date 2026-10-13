using System.Net.WebSockets;
using System.Text;
using SongLink.Host;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>契约 §3.6 queue.* 队列测试：PlayQueue 纯逻辑 + HostServer 命令/事件集成。</summary>
public class QueueTests
{
    [Fact]
    public void PlayQueue_add_top_remove_clear()
    {
        int events = 0;
        var q = new PlayQueue();
        q.Changed += (_args) => events++;
        q.Add("A", "x", "u1");
        q.Add("B", "y", "u2");
        Assert.Equal(2, q.Count);
        Assert.Equal("A", q.Snapshot()[0].Title);

        Assert.True(q.Top(1));                              // B 置顶
        Assert.Equal("B", q.Snapshot()[0].Title);
        Assert.True(q.RemoveAt(1));                         // 删掉 A
        Assert.Single(q.Snapshot());
        q.Clear();
        Assert.Empty(q.Snapshot());
        Assert.Equal(5, events);                            // add×2 + top + remove + clear
    }

    [Fact]
    public void PlayQueue_rejects_out_of_range()
    {
        var q = new PlayQueue();
        q.Add("A", "x", "u");
        Assert.False(q.RemoveAt(5));
        Assert.False(q.Top(5));
        Assert.False(q.Top(0));                             // 已在队首，置顶无效
    }

    [Fact]
    public async Task HostServer_queue_add_broadcasts_event()
    {
        int port = 52400 + Random.Shared.Next(300);
        const string Pin = "555555";
        await using var server = new HostServer(new FakeAdapter(), port, Pin);
        server.Start(port);
        await Task.Delay(200);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        await Send(ws, new Hello { Role = "remote", Device = "t", Pin = Pin, Id = "h" });
        await ReadUntil(ws, f => f is AuthResult a && a.Ok, 2000);

        await Send(ws, new Cmd { Id = "q1", Action = "queue.add", Args = new() { ["title"] = "晴天", ["artist"] = "周杰伦", ["by"] = "手机A" } });
        var ack = await ReadUntil(ws, f => f is Ack a && a.Ref == "q1", 2000) as Ack;
        Assert.True(ack is { Ok: true });
        var ev = await ReadUntil(ws, f => f is EventFrame e && e.Name == "queue-updated", 2000) as EventFrame;
        Assert.NotNull(ev);
        Assert.NotNull(ev!.Queue);
        Assert.Single(ev.Queue!);
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
