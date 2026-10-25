using System.Net.WebSockets;
using System.Text.Json.Nodes;
using SongLink.Host;
using SongLink.Host.DeepLink;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// "在线平台状态"事件的端到端测试。
///
/// <para>背景：<c>IAdapterDiagnostics.PlatformStatuses</c> 早就实现了，但**没有任何东西把它送到面板上** ——
/// 用户在面板上看到"搜出来了"，却不知道这台机器上的点歌通道到底能不能点得动
/// （有的平台只能搜不能点，有的会报"没版权/要会员"）。
/// Host 现在会在鉴权通过后、以及每次点歌之后推一个 <c>name = "platforms"</c> 的事件，
/// 这里验证这条链路真的通。</para>
/// </summary>
public class PlatformStatusEventTests
{
    [Fact]
    public async Task Client_receives_platform_capabilities_right_after_auth()
    {
        var port = 52900 + Random.Shared.Next(60);
        await using var server = new HostServer(new DiagAdapter(), port, pin: "999888");
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            await SendAsync(ws, new Hello
            {
                Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1,
            });

            var ev = await WaitForEventAsync(ws, "platforms");

            Assert.NotNull(ev);
            Assert.NotNull(ev!.Platforms);
            Assert.Equal(2, ev.Platforms!.Count);

            var first = ev.Platforms[0]!.AsObject();
            Assert.Equal("netease", first["kind"]!.GetValue<string>());
            Assert.Equal("网易云", first["name"]!.GetValue<string>());
            Assert.Equal("SearchAndPlay", first["capability"]!.GetValue<string>());

            // 第二个平台带"最近失败原因"，面板要能原样显示出来
            var second = ev.Platforms[1]!.AsObject();
            Assert.Equal("kugou", second["kind"]!.GetValue<string>());
            Assert.Equal("SearchOnly", second["capability"]!.GetValue<string>());
            Assert.Equal("客户端版本太老，不支持点歌", second["note"]!.GetValue<string>());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Adapter_without_online_platforms_sends_an_empty_platform_list()
    {
        // 不实现 IAdapterDiagnostics 的适配器（本地曲库 / 演示数据）也必须推一个**空**列表，
        // 否则面板会一直等一个永远不来的事件，标签行永远转圈。
        var port = 52970 + Random.Shared.Next(25);
        await using var server = new HostServer(new FakeAdapter(), port, pin: "999888");
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            await SendAsync(ws, new Hello
            {
                Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1,
            });

            var ev = await WaitForEventAsync(ws, "platforms");

            Assert.NotNull(ev);
            Assert.NotNull(ev!.Platforms);
            Assert.Empty(ev.Platforms!);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_failed_pick_pushes_the_updated_failure_reason()
    {
        // 点歌失败的原因会变（这次没版权、下次要会员），所以点歌之后要再推一次状态，
        // 面板上的标签才能立刻反映"为什么没响起来"。
        var adapter = new DiagAdapter { PickResult = false };
        var port = 53000 + Random.Shared.Next(25);
        await using var server = new HostServer(adapter, port, pin: "999888");
        try
        {
            server.Start(port);
            await Task.Delay(150);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
            await SendAsync(ws, new Hello
            {
                Role = "remote", Device = "t", Caps = { "cmd" }, Pin = "999888", Id = "h1", Seq = 1,
            });

            var first = await WaitForEventAsync(ws, "platforms");
            Assert.NotNull(first);
            var noteBefore = first!.Platforms![0]!.AsObject()["note"]?.GetValue<string>();

            // 点歌失败（适配器会自己写上失败原因，跟真实的 TakeoverAdapter 一样）
            await SendAsync(ws, new Cmd
            {
                Action = "pick",
                Id = "c1",
                Args = new JsonObject { ["title"] = "晴天", ["artist"] = "周杰伦" },
                Seq = 2,
            });

            // 等到带新 note 的那次推送
            var updated = await WaitForEventAsync(ws, "platforms", p =>
                p.Platforms?[0]?.AsObject()["note"]?.GetValue<string>() == "这首歌要会员");

            Assert.NotNull(updated);
            Assert.Equal("这首歌要会员",
                updated!.Platforms![0]!.AsObject()["note"]!.GetValue<string>());
            Assert.NotEqual("这首歌要会员", noteBefore);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ---------- helpers ----------

    /// <summary>一直读到收到指定名字的事件为止（可选地再按内容过滤）。</summary>
    private static async Task<EventFrame?> WaitForEventAsync(
        WebSocket ws, string name, Func<EventFrame, bool>? extra = null, int timeoutMs = 3000)
    {
        var buf = new byte[16 * 1024];
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var r = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            var json = System.Text.Encoding.UTF8.GetString(buf, 0, r.Count);
            var f = FrameCodec.Parse(json);
            if (f is EventFrame ev && ev.Name == name && (extra is null || extra(ev)))
                return ev;
        }
        return null;
    }

    private static async Task SendAsync(WebSocket ws, Frame f)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(f, FrameCodec.Options);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>实现了诊断接口的假适配器：能报出平台能力，点歌可以按需失败。</summary>
    private sealed class DiagAdapter : IPlayerAdapter, IAdapterDiagnostics
    {
        public string AppId => "diag";

        public AdapterCapability Caps =>
            AdapterCapability.ReadMeta | AdapterCapability.Transport |
            AdapterCapability.Pick | AdapterCapability.Search;

        /// <summary>点歌是否成功。测试用它来触发"失败原因"这条路径。</summary>
        public bool PickResult { get; set; } = true;

        /// <summary>最近一次点歌失败的原因。</summary>
        public string? LastPickFailureReason { get; set; }

        /// <summary>
        /// 平台状态是**每次现算**的 —— 和真实的 <c>TakeoverAdapter.PlatformStatuses</c> 一样。
        /// 如果这里写死成固定值，Host 推出去的 note 就不会跟着点歌结果变，
        /// 测出来的"点歌后更新状态"就是假的。
        /// </summary>
        public IReadOnlyList<PlatformStatus> PlatformStatuses => new[]
        {
            new PlatformStatus("netease", "网易云", PlatformCapability.SearchAndPlay, LastPickFailureReason),
            new PlatformStatus("kugou", "酷狗", PlatformCapability.SearchOnly, "客户端版本太老，不支持点歌"),
        };

        public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default)
            => Task.FromResult<NowPlaying?>(null);

        public Task<bool> Play(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Pause(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Next(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Prev(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Stop(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> Pick(string title, string artist, CancellationToken ct = default)
        {
            // 点不动的时候要说清楚为什么，跟真实适配器一个口径
            if (!PickResult) LastPickFailureReason = "这首歌要会员";
            return Task.FromResult(PickResult);
        }

        public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());

        public Task<double> GetVolume(CancellationToken ct = default) => Task.FromResult(1.0);
        public Task<bool> SetVolume(double volume, CancellationToken ct = default) => Task.FromResult(true);
    }
}
