using System;
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
        
        // 模拟完整的重连流程（简化版）
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
    
    private static async Task<Frame?> ReceiveUntilAsync(ClientWebSocket ws, Func<Frame, bool> predicate)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.EndOfMessage) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        }
        
        var frame = FrameCodec.Parse(sb.ToString());
        return frame != null && predicate(frame) ? frame : null;
    }
}

// Fixture for managing free ports
public sealed class HostServerFixture : IDisposable
{
    private const int BasePort = 52400;
    private int _currentPortOffset = 0;
    
    public int FreePort => BasePort + _currentPortOffset++;
    
    public void Dispose() { }
}
