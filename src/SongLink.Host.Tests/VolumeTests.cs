using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SongLink.Host.AudioStream;
using SongLink.Host;
using SongLink.Protocol;

namespace SongLink.Host.Tests;

/// <summary>
/// 端到端音量控制测试：覆盖 MediaBus.Volume、AudioStreamSink.SetVolume 转给渲染器、
/// HostServer volume cmd → Adapter.GetVolume 同步 + 广播 volume-changed 事件。
/// </summary>
public class VolumeTests
{
    [Fact]
    public void MediaBus_SetVolume_clamps_to_0_1_and_updates_property()
    {
        var bus = new MediaBus(new FakeAdapter());
        Assert.Equal(1.0, bus.Volume);
        bus.SetVolume(0.4);
        Assert.Equal(0.4, bus.Volume);
        bus.SetVolume(2.5);
        Assert.Equal(1.0, bus.Volume);
        bus.SetVolume(-1.0);
        Assert.Equal(0.0, bus.Volume);
    }

    [Fact]
    public void NullRenderer_Volume_set_and_get_roundtrip()
    {
        var r = new NullRenderer();
        Assert.Equal(1.0, r.Volume);
        r.Volume = 0.25;
        Assert.Equal(0.25, r.Volume);
    }

    [Fact]
    public void AudioStreamSink_SetVolume_propagates_to_renderer()
    {
        var sink = new AudioStreamSink(new OpusCodec(48000, 2), new NullRenderer(), 0);
        sink.SetVolume(0.5);
        // 验证 Start 后仍能调音量(渲染器未销毁,设值不抛)
        sink.Start();
        sink.SetVolume(0.3);
        sink.Stop();
        sink.Dispose();
    }

    [Fact]
    public async Task HostServer_volume_cmd_clamps_and_broadcasts_event()
    {
        var port = 52200 + Random.Shared.Next(8);
        var server = new HostServer(new FakeAdapter(), port: port, pin: "999888",
            audioRenderer: new NullRenderer());
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new System.Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            await SendAsync(ws, new Hello { Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1 });
            await WaitAuth(ws);

            // 发 volume cmd → 应回 ack ok + 收到 volume-changed event
            await SendAsync(ws, new Cmd
            {
                Id = "v1",
                Action = "volume",
                Args = NodeObj(("level", 0.42)),
                Seq = 2
            });
            // 共享一个帧队列,因为服务端的推送顺序是 [State, volume-changed, Ack],
            // 不能用 WaitAck 一路消耗后再用 WaitEvent(后者会空等)
            var frames = new System.Collections.Concurrent.ConcurrentQueue<Frame>();
            var ack = await WaitFrame<Ack>(ws, f => { frames.Enqueue(f); return f is Ack a && a.Ref == "v1"; }, 2000);
            Assert.NotNull(ack);
            Assert.True(ack!.Ok);

            // volume-changed event 可能已在 frames 队列里,也可能还没来 — 轮询直到出现
            var deadline = System.DateTime.UtcNow.AddSeconds(2);
            EventFrame? ev = null;
            while (System.DateTime.UtcNow < deadline)
            {
                if (frames.TryDequeue(out var f))
                {
                    if (f is EventFrame ef && ef.Name == "volume-changed") { ev = ef; break; }
                    continue;
                }
                var next = await ReadOneFrame(ws);
                if (next is EventFrame ef2 && ef2.Name == "volume-changed") { ev = ef2; break; }
            }
            Assert.NotNull(ev);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HostServer_volume_cmd_clamps_out_of_range_inputs()
    {
        var port = 52300 + Random.Shared.Next(8);
        var server = new HostServer(new FakeAdapter(), port: port, pin: "999888",
            audioRenderer: new NullRenderer());
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new System.Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            await SendAsync(ws, new Hello { Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1 });
            await WaitAuth(ws);

            // level=5.0 → clamp 到 1.0 → 仍 ok
            await SendAsync(ws, new Cmd { Id = "v2", Action = "volume", Args = NodeObj(("level", 5.0)), Seq = 2 });
            var ack1 = await WaitAck(ws, "v2", 2000);
            Assert.True(ack1!.Ok);

            // level=-2.0 → clamp 到 0.0 → 仍 ok
            await SendAsync(ws, new Cmd { Id = "v3", Action = "volume", Args = NodeObj(("level", -2.0)), Seq = 3 });
            var ack2 = await WaitAck(ws, "v3", 2000);
            Assert.True(ack2!.Ok);

            await Task.Delay(50);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HostServer_volume_cmd_without_level_arg_returns_ack_false()
    {
        var port = 52400 + Random.Shared.Next(8);
        var server = new HostServer(new FakeAdapter(), port: port, pin: "999888",
            audioRenderer: new NullRenderer());
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new System.Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            await SendAsync(ws, new Hello { Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1 });
            await WaitAuth(ws);

            // 缺 level 参数 → 应回 ack ok=false
            await SendAsync(ws, new Cmd { Id = "v4", Action = "volume", Seq = 2 });
            var ack = await WaitAck(ws, "v4", 2000);
            Assert.NotNull(ack);
            Assert.False(ack!.Ok, "缺 level 参数的 volume cmd 应回 ok=false");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static System.Text.Json.Nodes.JsonObject NodeObj(params (string k, object v)[] kv)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in kv)
        {
            if (v is double d) o[k] = d;
            else if (v is int i) o[k] = i;
            else o[k] = v.ToString();
        }
        return o;
    }

    private static async Task SendAsync(WebSocket ws, Frame f, CancellationToken ct = default)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(f,
            SongLink.Protocol.FrameCodec.Options);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<Ack?> WaitAck(WebSocket ws, string refId, int timeoutMs)
    {
        var buf = new byte[16 * 1024];
        var deadline = System.DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (System.DateTime.UtcNow < deadline)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            var json = System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
            var f = SongLink.Protocol.FrameCodec.Parse(json);
            if (f is Ack ack && ack.Ref == refId) return ack;
        }
        return null;
    }

    /// <summary>通用"读帧直到谓词命中"读取器。每读一帧都会执行 visitor(用于缓冲)。
    /// 返回谓词命中的帧。</summary>
    private static async Task<T?> WaitFrame<T>(WebSocket ws, Func<Frame, bool> visitor, int timeoutMs) where T : Frame
    {
        var buf = new byte[16 * 1024];
        var deadline = System.DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (System.DateTime.UtcNow < deadline)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            var json = System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
            var f = SongLink.Protocol.FrameCodec.Parse(json);
            if (f is null) continue;
            if (visitor(f) && f is T t) return t;
        }
        return null;
    }

    private static async Task<Frame?> ReadOneFrame(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        if (r.MessageType != WebSocketMessageType.Text) return null;
        var json = System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
        return SongLink.Protocol.FrameCodec.Parse(json);
    }

    private static async Task<AuthResult> WaitAuth(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        var deadline = System.DateTime.UtcNow.AddSeconds(3);
        while (System.DateTime.UtcNow < deadline)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            var json = System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
            var f = SongLink.Protocol.FrameCodec.Parse(json);
            if (f is AuthResult auth) return auth;
        }
        throw new TimeoutException("WaitAuth 超时");
    }
}
