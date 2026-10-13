using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SongLink.Host.AudioStream;
using SongLink.Host;
using SongLink.Protocol;

namespace SongLink.Host.Tests;

/// <summary>
/// HostServer.AudioStart 真实起 UDP 监听器 (修了一处之前的 bug — audio:start 后调
/// _audioSink.Start() 启 UDP,而非只 log)。这里验证修后 sink.IsRunning + LocalPort > 0 +
/// FramesReceived 真的能 +1。
/// </summary>
public class HostServerAudioStartTests
{
    [Fact]
    public async Task AudioStart_actually_binds_udp_port_and_sink_Start_is_called()
    {
        // 用固定端口避免 os-chosen 端口 + xUnit 并发测试时的端口竞争
        var port = 51990 + Random.Shared.Next(8);
        var server = new HostServer(new FakeAdapter(), port: port, pin: "999888",
            audioRenderer: new NullRenderer());
        try
        {
            server.Start(port);
            // 等 WS 服务端起来
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            var serverPort = ReadHostServerPort(server);
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{serverPort}/"), CancellationToken.None);
            await SendAsync(ws, new Hello { Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1 });
            await WaitAuth(ws);

            // audio:start 应启 UDP sink
            await SendAsync(ws, new AudioStart { Id = "a1", Rate = 16000, Ch = 1, FrameMs = 20, Port = 51000, Seq = 2 });
            // 等服务端启动 sink
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (server.AudioSink is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.NotNull(server.AudioSink);
            var sink = server.AudioSink!;
            Assert.True(sink.LocalPort > 0, "Audio:start 后 UDP 监听器应已绑定非零本地端口");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AudioStart_then_send_udp_frame_increments_FramesReceived()
    {
        var port = 52000 + Random.Shared.Next(8);
        var server = new HostServer(new FakeAdapter(), port: port, pin: "999888",
            audioRenderer: new NullRenderer());
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            var serverPort = ReadHostServerPort(server);
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{serverPort}/"), CancellationToken.None);
            await SendAsync(ws, new Hello { Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1 });
            await WaitAuth(ws);

            await SendAsync(ws, new AudioStart { Id = "a1", Rate = 16000, Ch = 1, FrameMs = 20, Port = 51000, Seq = 2 });

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while ((server.AudioSink is null || server.AudioSink!.LocalPort == 0) && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.NotNull(server.AudioSink);
            var sinkPort = server.AudioSink!.LocalPort;

            // 构造一帧 raw PCM (1920 B) + §4 header,FlagRawPcm=1
            var pcm = new byte[1920];
            var frame = AudioStreamWire.Assemble(
                flags: (byte)(AudioStreamWire.FlagFirst | AudioStreamWire.FlagLast | AudioStreamWire.FlagRawPcm),
                seq: 1u, pts: 16_000UL, opusFrame: pcm);

            using var sender = new UdpClient(0);
            await sender.SendAsync(frame, new IPEndPoint(IPAddress.Loopback, sinkPort));

            // 等 sink 计数
            var latched = 0L;
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                latched = server.AudioSink!.FramesReceived;
                if (latched >= 1) break;
                await Task.Delay(20);
            }
            Assert.True(latched >= 1, "AudioStart 启的 UDP 接收器应能 +1 FramesReceived");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>setSourceMode(PC_LOCAL) 在 audio:start 之后 → 应回 ack ok=true,且 sink 仍存活(UDP 句柄不释放,仅停渲染器)。
    /// 验证契约 #12:进 PHONE_STREAM 冻结 PC 出声,回 PC_LOCAL 交还输出。</summary>
    [Fact]
    public async Task SetSourceMode_PC_LOCAL_after_audio_start_does_not_dispose_sink()
    {
        var port = 52100 + Random.Shared.Next(8);
        var server = new HostServer(new FakeAdapter(), port: port, pin: "999888",
            audioRenderer: new NullRenderer());
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            var serverPort = ReadHostServerPort(server);
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{serverPort}/"), CancellationToken.None);
            await SendAsync(ws, new Hello { Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1 });
            await WaitAuth(ws);

            // audio:start 应启 UDP sink
            await SendAsync(ws, new AudioStart { Id = "a1", Rate = 16000, Ch = 1, FrameMs = 20, Port = 51000, Seq = 2 });
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while ((server.AudioSink is null || server.AudioSink!.LocalPort == 0) && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.NotNull(server.AudioSink);
            var sinkRef = server.AudioSink!;
            Assert.True(sinkRef.LocalPort > 0);

            // setSourceMode(PC_LOCAL) → 不应 Dispose sink,只调 Stop()(NullRenderer.Stop 无副作用)
            await SendAsync(ws, new Cmd { Id = "m1", Action = "setSourceMode", Args = NodeObj(("mode", "PC_LOCAL")), Seq = 3 });
            var ack = await WaitAck(ws, "m1", 2000);
            Assert.NotNull(ack);
            Assert.True(ack!.Ok, "setSourceMode PC_LOCAL 应回 ack ok=true");

            // 等一下再确认 sink 还活着(LocalPort 应仍 = 51000)
            await Task.Delay(100);
            Assert.NotNull(server.AudioSink);
            Assert.Same(sinkRef, server.AudioSink);   // 没换实例 = 没 Dispose
            Assert.Equal(51000, server.AudioSink!.LocalPort);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static System.Text.Json.Nodes.JsonObject NodeObj(params (string k, string v)[] kv)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    private static async Task<Ack?> WaitAck(WebSocket ws, string refId, int timeoutMs)
    {
        var buf = new byte[16 * 1024];
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            var json = System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
            var f = SongLink.Protocol.FrameCodec.Parse(json);
            if (f is Ack ack && ack.Ref == refId) return ack;
        }
        return null;
    }

    /// <summary>读 server.Port (public property);songLink.Host 公共).</summary>
    private static int ReadHostServerPort(HostServer server) => server.Port;

    private static async Task SendAsync(WebSocket ws, Frame f, CancellationToken ct = default)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(f,
            SongLink.Protocol.FrameCodec.Options);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<AuthResult> WaitAuth(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
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
