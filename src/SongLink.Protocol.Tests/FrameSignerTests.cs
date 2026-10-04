using System.Net.WebSockets;
using System.Text;
using SongLink.Host;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>契约 §6 每帧 HMAC 校验：FrameSigner 纯逻辑 + HostServer 入站验签强制路径。</summary>
public class FrameSignerTests
{
    [Fact]
    public void Sign_then_verify_roundtrip()
    {
        var cmd = new Cmd { Id = "x1", Action = "next" };
        FrameSigner.Sign("token-abc", cmd);
        Assert.NotNull(cmd.Sig);
        Assert.True(FrameSigner.Verify("token-abc", cmd));
    }

    [Fact]
    public void Verify_fails_on_tamper()
    {
        var cmd = new Cmd { Id = "x1", Action = "next", Args = new() { ["title"] = "A" } };
        FrameSigner.Sign("k", cmd);
        cmd.Args!["title"] = "B";                        // 篡改载荷
        Assert.False(FrameSigner.Verify("k", cmd));
    }

    [Fact]
    public void Verify_fails_on_wrong_key_or_missing_sig()
    {
        var cmd = new Cmd { Id = "x1", Action = "next" };
        FrameSigner.Sign("k1", cmd);
        Assert.False(FrameSigner.Verify("k2", cmd));     // 错密钥
        var unsigned = new Cmd { Id = "x2", Action = "next" };
        Assert.False(FrameSigner.Verify("k1", unsigned)); // 无签名
    }

    [Fact]
    public void Signature_is_stable_and_sig_excluded_from_canonical()
    {
        var cmd = new Cmd { Id = "x1", Action = "next" };
        var s1 = FrameSigner.Compute("k", cmd);
        cmd.Sig = "GARBAGE";                              // sig 不应参与 canonical
        var s2 = FrameSigner.Compute("k", cmd);
        Assert.Equal(s1, s2);
    }

    [Fact]
    public async Task HostServer_enforces_signature_when_required()
    {
        int port = 52100 + Random.Shared.Next(300);
        const string Pin = "333333";
        await using var server = new HostServer(new FakeAdapter(), port, Pin, requireFrameSig: true);
        server.Start(port);
        await Task.Delay(200);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None);
        await Send(ws, new Hello { Role = "remote", Device = "t", Pin = Pin, Id = "h" });

        string token = await ReadUntil(ws, f => f is AuthResult a && a.Ok, 2000) switch
        { AuthResult ar => ar.Token ?? "", _ => "" };
        Assert.False(string.IsNullOrEmpty(token), "应拿到 token");

        // 未签名命令 → 被拒 AUTH_FAILED
        await Send(ws, new Cmd { Id = "u1", Action = "next" });
        var rej = await ReadUntil(ws, f => f is ErrorFrame e && e.Ref == "u1" && e.Code == "AUTH_FAILED", 2000);
        Assert.NotNull(rej);

        // 正确签名命令 → ack
        var good = new Cmd { Id = "g1", Action = "next" };
        FrameSigner.Sign(token, good);
        await Send(ws, good);
        var ack = await ReadUntil(ws, f => f is Ack a && a.Ref == "g1", 2000) as Ack;
        Assert.True(ack is { Ok: true });
    }

    // —— helpers ——
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
            try
            {
                do { r = await ws.ReceiveAsync(buf, cts.Token); sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); }
                while (!r.EndOfMessage);
            }
            catch { return null; }
            var f = FrameCodec.Parse(sb.ToString());
            if (f is not null && pred(f)) return f;
        }
        return null;
    }
}
