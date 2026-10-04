using System.Text.Json.Nodes;
using Json.Schema;
using SongLink.Host;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>
/// 契约/回归测试：把真实发出的帧拿去用 songlink-frame.schema.json 校验，
/// 并锁死评审 #2（跨栈 v 字段）与 #3（状态去抖）的修复。
/// </summary>
public class ContractTests
{
    private static readonly JsonSchema Schema =
        JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "songlink-frame.schema.json")));

    private static bool Valid(string json)
        => Schema.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    public static IEnumerable<object[]> AllFrames()
    {
        Frame[] frames =
        {
            new Hello { Role="remote", Device="d", Caps=new(){ "cmd","state" }, Seq=1 },
            new Cmd { Id="c1", Action="pick", Args=new JsonObject{["title"]="晴天",["artist"]="周杰伦"}, Seq=2 },
            new Ack { Ref="c1", Ok=true, Seq=3 },
            new State { Now=new NowPlaying{ Title="晴天", Artist="周杰伦", Pos=1.2, Dur=269, Status="playing" }, Seq=4 },
            new Lyric { TrackId="l1", Lines=new(){ new LyricLine{T=0,X="a"}, new LyricLine{T=3.5,X="b"} }, Seq=5 },
            new EventFrame { Name="source-mode-changed", Mode="PHONE_STREAM", Seq=6 },
            new Art { Ref="img:a1", Mime="image/jpeg", Size=1234, Seq=7 },
            new SrcMeta { Title="t", Artist="a", Pos=1, Dur=100, Status="playing", Seq=8 },
            new AudioStart { Codec="opus", Rate=48000, Ch=2, FrameMs=20, Port=51000, Seq=9 },
            new ErrorFrame { Ref="c1", Code="PICK_UNSUPPORTED", Hint="h", Seq=10 },
            new Ping { Seq=11 },
            new Hello { Role="remote", Device="d", Pin="246810", Seq=12 },
            new AuthResult { Ref="h1", Ok=true, Token="ABCDEF0123456789", Seq=13 },
            new SearchResults { Ref="s1", Query="Yellow", Results=new(){ new SearchResultItem{ Title="Yellow", Artist="Coldplay", Album="Parachutes", Dur=267 } }, Seq=14 },
        };
        foreach (var f in frames) yield return new object[] { f };
    }

    [Theory]
    [MemberData(nameof(AllFrames))]
    public void Emitted_frame_conforms_to_schema(Frame frame)
    {
        var json = FrameCodec.Serialize(frame);
        Assert.True(Valid(json), $"帧不符合 schema：{json}\n{Detail(json)}");
    }

    [Theory]
    [InlineData("{\"v\":2,\"type\":\"ping\",\"seq\":1}")]                 // v 非 1
    [InlineData("{\"type\":\"ack\",\"seq\":1,\"ref\":\"x\"}")]             // 缺 ok
    [InlineData("{\"v\":1,\"type\":\"state\",\"seq\":1,\"now\":{\"status\":\"weird\"}}")] // status 非法
    [InlineData("{\"v\":1,\"type\":\"cmd\",\"seq\":1,\"action\":\"pick\"}")] // 缺 id
    [InlineData("{\"v\":1,\"type\":\"bogus\",\"seq\":1}")]                // 未知 type
    [InlineData("{\"v\":1,\"type\":\"authResult\",\"seq\":1,\"ref\":\"h1\"}")] // authResult 缺 ok
    [InlineData("{\"v\":1,\"type\":\"searchResults\",\"seq\":1,\"ref\":\"s1\",\"query\":\"x\"}")] // searchResults 缺 results
    public void Invalid_frame_rejected(string json) => Assert.False(Valid(json));

    // ---- #2 回归：跨栈 v 字段 ----
    [Fact]
    public void Parse_accepts_frame_missing_v()
    {
        // 模拟 Kotlin encodeDefaults=false 省略 v 的旧行为：.NET 应仍接受
        var f = FrameCodec.Parse("{\"type\":\"cmd\",\"seq\":1,\"id\":\"x\",\"action\":\"next\"}");
        Assert.IsType<Cmd>(f);
    }

    [Fact]
    public void Parse_rejects_explicit_wrong_v()
    {
        Assert.Null(FrameCodec.Parse("{\"v\":2,\"type\":\"cmd\",\"seq\":1,\"id\":\"x\",\"action\":\"next\"}"));
    }

    [Fact]
    public void Serialize_always_includes_v()
    {
        var json = FrameCodec.Serialize(new Ping { Seq = 1 });
        Assert.Contains("\"v\":1", json);
    }

    // ---- #3：状态去抖/节流 ----
    [Fact]
    public void StateGate_emits_on_change_and_throttles_when_steady()
    {
        var gate = new StateGate(TimeSpan.FromSeconds(1));
        var t0 = DateTimeOffset.UtcNow;
        var a = new NowPlaying { Title = "A", Artist = "x", Status = "playing" };

        Assert.True(gate.ShouldEmit(a, t0));                 // 首次：发
        Assert.False(gate.ShouldEmit(a, t0.AddMilliseconds(250))); // 未变：不发
        Assert.False(gate.ShouldEmit(a, t0.AddMilliseconds(600)));
        Assert.True(gate.ShouldEmit(a, t0.AddMilliseconds(1000)));  // 心跳：发
        var b = new NowPlaying { Title = "B", Artist = "x", Status = "playing" };
        Assert.True(gate.ShouldEmit(b, t0.AddMilliseconds(1100)));  // 换曲：立即发
    }

    // ---- #6：PIN 配对 + token 鉴权 ----
    [Fact]
    public void AuthManager_validates_pin()
    {
        var auth = new AuthManager("246810");
        Assert.True(auth.ValidatePin("246810"));
        Assert.False(auth.ValidatePin("000000"));
        Assert.False(auth.ValidatePin(null));
        Assert.False(auth.ValidatePin(""));
    }

    [Fact]
    public void AuthManager_issues_and_validates_token()
    {
        var auth = new AuthManager("111111");
        var token = auth.IssueToken("Pixel-7");
        Assert.True(auth.ValidateToken(token));
        Assert.False(auth.ValidateToken("not-a-real-token"));
        Assert.False(auth.ValidateToken(null));
    }

    [Fact]
    public void AuthManager_generates_6_digit_pin_by_default()
    {
        var auth = new AuthManager();
        Assert.Matches("^[0-9]{6}$", auth.Pin);
    }

    // ---- #4：ArtStore 路径安全 + 取图 ----
    [Theory]
    [InlineData("demo-0", true)]
    [InlineData("a.b_c-1", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ArtStore_ref_safety(string? r, bool safe) => Assert.Equal(safe, ArtStore.IsSafeRef(r));

    [Fact]
    public void ArtStore_resolves_existing_and_rejects_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "songlink-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(dir, "demo-0.png"), png);
            var store = new ArtStore(dir);

            var got = store.Get("demo-0");
            Assert.NotNull(got);
            Assert.Equal("image/png", got!.Value.Mime);
            Assert.Equal(png, got.Value.Bytes);

            Assert.Null(store.Get("missing"));        // 不存在
            Assert.Null(store.Get("../secret"));      // 穿越
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- #8：广播前克隆，不改共享实例 ----
    [Fact]
    public void NowPlaying_Clone_is_independent()
    {
        var a = new NowPlaying { Title = "T", Artist = "A", Pos = 1, SourceMode = "PC_LOCAL" };
        var b = a.Clone();
        b.SourceMode = "PHONE_STREAM"; b.Pos = 99; b.Title = "T2";
        Assert.Equal("PC_LOCAL", a.SourceMode);
        Assert.Equal(1, a.Pos);
        Assert.Equal("T", a.Title);
        Assert.Equal("PHONE_STREAM", b.SourceMode);
    }

    // ---- 契约 §7：断线补发缓冲 ----
    [Fact]
    public void ReplayBuffer_since_and_eviction()
    {
        var buf = new ReplayBuffer(capacity: 3);
        for (long s = 1; s <= 5; s++) buf.Add(new State { Now = new NowPlaying { Title = "t" + s }, Seq = s });

        Assert.Equal(3, buf.Count);                                   // 容量 3，淘汰最旧两条
        Assert.Equal(new long[] { 3, 4, 5 }, buf.Since(2).Select(f => f.Seq)); // seq>2 且升序
        Assert.Single(buf.Since(4));                                  // 只有 5
        Assert.Empty(buf.Since(5));                                   // 无更新
    }

    // ---- #11：唇同步 offset ----
    [Fact]
    public void LyricService_offset_per_track_and_global()
    {
        var dir = Path.Combine(Path.GetTempPath(), "songlink-lyr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "晴天 - 周杰伦.lrc"), "[00:03.20]故事的小黄花\n[00:06.50]从出生那年就飘着\n");
            var svc = new LyricService(dir);

            Assert.Equal(0, svc.GetOffset("晴天", "周杰伦"));                 // 默认无偏移
            svc.SetOffset("晴天", "周杰伦", 250);
            Assert.Equal(250, svc.GetOffset("晴天", "周杰伦"));

            var ly = svc.TryGet("晴天", "周杰伦");
            Assert.NotNull(ly);
            Assert.Equal(250, ly!.OffsetMs);                                  // 帧带偏移
            Assert.Equal(2, ly.Lines.Count);

            svc.SetOffset(null, null, 100);                                  // 全局
            Assert.Equal(100, svc.GetOffset("别的歌", "X"));                  // 无每曲偏移 → 用全局
            Assert.Equal(250, svc.GetOffset("晴天", "周杰伦"));               // 每曲优先于全局
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string Detail(string json)
    {
        var r = Schema.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List });
        try { return System.Text.Json.JsonSerializer.Serialize(r); }
        catch { return r.ToString() ?? ""; }
    }
}
