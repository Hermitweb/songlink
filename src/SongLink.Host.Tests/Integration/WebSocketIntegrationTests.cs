using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SongLink.Host;
using SongLink.Protocol;

namespace SongLink.Host.Tests.Integration;

/// <summary>
/// WebSocket 集成测试 - 端到端协议流程验证
/// </summary>
public class WebSocketIntegrationTests : IClassFixture<HostServerFixture>
{
    private readonly HostServerFixture _fixture;
    
    public WebSocketIntegrationTests(HostServerFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task FullProtocolFlow_HelloThenCmdThenAck()
    {
        var port = _fixture.FreePort;
        
        // 1. 启动 Host Server
        await using var server = new FakeHostServer(port);
        server.Start();
        
        // 2. 等待 Server 就绪
        await Task.Delay(500);
        
        // 3. 客户端连接并发送 Hello
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        
        // 发送 Hello 帧
        var hello = new Hello 
        { 
            Role = "remote", 
            Device = "test-client", 
            Id = "h-test-1" 
        };
        var helloJson = FrameCodec.Serialize(hello);
        await SendFrame(client, helloJson);
        
        // 4. 接收 AuthResult
        var authResult = await ReceiveUntilAsync(client, f => f is AuthResult a && a.Ok);
        Assert.NotNull(authResult);
        Assert.IsType<AuthResult>(authResult);
        var auth = (AuthResult)authResult!;
        Assert.True(auth.Ok);
        
        // 5. 发送 Cmd 命令
        var cmd = new Cmd 
        { 
            Action = "toggle", 
            Id = "c-test-1" 
        };
        var cmdJson = FrameCodec.Serialize(cmd);
        await SendFrame(client, cmdJson);
        
        // 6. 接收 Ack
        var ack = await ReceiveUntilAsync(client, f => f is Ack ackFrame && ackFrame.Ref == "c-test-1");
        Assert.NotNull(ack);
        Assert.IsType<Ack>(ack);
        Assert.Equal("c-test-1", ((Ack)ack!).Ref);
    }
    
    [Fact]
    public async Task Reconnect_WithToken_RestoresState()
    {
        var port = _fixture.FreePort + 1;

        // 之前这个用例只分配了端口号却没启动任何服务器，必然连不上
        await using var server = new FakeHostServer(port);
        server.Start();
        await Task.Delay(300);

        // 模拟完整的重连流程：先配对拿 token，再用新连接携 token 重连
        using var client1 = new ClientWebSocket();
        using var client2 = new ClientWebSocket();

        await client1.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        await client2.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);

        // 第一个客户端发送 Hello 并获取 token
        var hello1 = new Hello { Role = "remote", Id = "h1", Token = null, Pin = "123456" };
        await SendFrame(client1, FrameCodec.Serialize(hello1));

        var auth1 = await ReceiveUntilAsync(client1, f => f is AuthResult);
        var token = (auth1 as AuthResult)?.Token;
        Assert.NotNull(token);
        Assert.True((auth1 as AuthResult)!.Ok);

        // 第二个客户端使用 token 重连
        var hello2 = new Hello { Role = "remote", Id = "h2", Token = token };
        await SendFrame(client2, FrameCodec.Serialize(hello2));

        var auth2 = await ReceiveUntilAsync(client2, f => f is AuthResult);
        Assert.NotNull(auth2);
        var auth2Obj = (AuthResult)auth2!;
        Assert.True(auth2Obj.Ok);
    }
    
    private static async Task SendFrame(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }
    
    /// <summary>
/// 收帧直到 predicate 命中或超时。
/// 注意：WebSocket 消息可能被拆成多个帧，必须读到 EndOfMessage 再按整条消息解析；
/// 且要先 Append 再判断 EndOfMessage（否则单条消息会丢掉内容）。
/// </summary>
private static async Task<Frame?> ReceiveUntilAsync(
        ClientWebSocket ws, Func<Frame, bool> predicate, int timeoutMs = 5000)
    {
        var buffer = new byte[8192];

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage);

                var frame = FrameCodec.Parse(sb.ToString());
                if (frame is not null && predicate(frame)) return frame;
            }
        }
        catch (OperationCanceledException) { return null; }   // 超时
        catch (WebSocketException) { return null; }
    }
}

// Fixture for managing free ports
public sealed class HostServerFixture : IDisposable
{
    /// <summary>
    /// 真的去问系统要一个当前空闲的端口。
    /// </summary>
    /// <remarks>
    /// 原来这里是 <c>52400 + _offset++</c> —— 名叫 FreePort 但压根没检查端口是否被占用。
    /// 只要 52400~52499 里任何一个被别的进程（IDE、残留的 Host、并行跑的另一个测试类）占着，
    /// 对应用例就会随机挂掉。这是长期存在的偶发失败来源，排查成本很高，根因就在这。
    /// </remarks>
    public int FreePort
    {
        get
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
    }

    public void Dispose() { }
}
