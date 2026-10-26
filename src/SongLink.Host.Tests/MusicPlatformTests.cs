using SongLink.Host;
using SongLink.Host.DeepLink;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// 三平台（网易云 / QQ音乐 / 酷狗）搜索解析与点歌路由的测试。
///
/// 覆盖的三个要害：
///   1. **解析出的 ID 必须能直接用于播放** —— 网易云要 id、QQ音乐要 mid、酷狗要 hash；
///      取错字段点歌会失败或放错歌。
///   2. **搜索结果必须带 source/sourceId** —— 点歌时靠它路由，不做二次名称匹配。
///   3. **点歌成功必须等于"客户端真的在放这首歌"** —— 命令发出去不等于有声音。
///      网易云实测过"切歌成功但状态 Stop（没版权）"，QQ音乐非会员点 VIP 歌会弹付费窗。
/// </summary>
public class MusicPlatformTests
{
    // ------------------------------------------------------------ 网易云

    private const string NeteaseJson = """
    {"result":{"songs":[
      {"id":186016,"name":"晴天","artists":[{"name":"周杰伦"}],"album":{"name":"叶惠美"},"duration":269000},
      {"id":5,"name":"稻香","artists":[{"name":"周杰伦"}],"album":{"name":"魔杰座"},"duration":223000},
      {"name":"没有 id 的脏数据","artists":[{"name":"某人"}]}
    ]}}
    """;

    [Fact]
    public void Netease_search_parses_id_name_artist_album_and_seconds()
    {
        var items = NeteasePlatform.NeteaseParser.Parse(NeteaseJson, 10);

        Assert.Equal(2, items.Count);   // 没有 id 的脏数据被跳过
        var q = items[0];
        Assert.Equal("晴天", q.Title);
        Assert.Equal("周杰伦", q.Artist);
        Assert.Equal("叶惠美", q.Album);
        Assert.Equal("netease", q.Source);
        Assert.Equal("186016", q.SourceId);
        // 毫秒 → 秒
        Assert.Equal(269.0, q.Dur!.Value, 1e-6);
    }

    [Fact]
    public async Task Netease_play_refuses_the_disproven_deep_link_by_default()
    {
        // 实测：orpheus:// 下发后客户端不切歌、静默失效。
        // 所以默认必须拒绝，而不是"看起来成功、实际没声音"。
        var launched = new List<string>();
        var p = new NeteasePlatform { Launch = launched.Add };

        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", SourceId = "186016" }));
        Assert.Empty(launched);
        Assert.Contains("深链接", p.LastFailureReason!);
    }

    [Fact]
    public async Task Netease_play_can_be_forced_to_use_the_deep_link()
    {
        var launched = new List<string>();
        var p = new NeteasePlatform { Launch = launched.Add, AllowDeepLink = true };

        Assert.True(await p.PlayAsync(new SearchResultItem { Title = "晴天", SourceId = "186016" }));
        Assert.Equal(new[] { "orpheus://song/186016" }, launched);
    }

    [Fact]
    public async Task Netease_play_fails_without_id()
    {
        var p = new NeteasePlatform { Launch = _ => throw new InvalidOperationException("不该被调用") };
        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天" }));
    }

    // ------------------------------------------------------------ QQ 音乐

    /// <summary>实测可用的联想搜索返回格式（data.song.itemlist）。</summary>
    private const string QqJson = """
    {"code":0,"data":{"song":{"count":2,"itemlist":[
      {"id":"97773","mid":"0039MnYb0qxYhV","name":"晴天","singer":"周杰伦"},
      {"id":"340236384","mid":"001OAxbu2X5Sop","name":"晴天（钢琴版）","singer":"某人"}
    ]}}}
    """;

    [Fact]
    public void Qq_search_uses_mid_and_carries_singer()
    {
        var items = QqMusicPlatform.Parser.Parse(QqJson, 10);

        Assert.Equal(2, items.Count);
        var q = items[0];
        Assert.Equal("晴天", q.Title);
        Assert.Equal("周杰伦", q.Artist);
        Assert.Equal("qq", q.Source);
        // mid 才是详情接口/播放器认的 id（实测：数字 id 也能用，但 mid 更稳）
        Assert.Equal("0039MnYb0qxYhV", q.SourceId);
    }

    [Fact]
    public void Qq_search_falls_back_to_numeric_id_when_mid_is_missing()
    {
        var json = """{"data":{"song":{"itemlist":[{"id":"97773","name":"晴天","singer":"周杰伦"}]}}}""";
        var items = QqMusicPlatform.Parser.Parse(json, 10);

        Assert.Single(items);
        Assert.Equal("97773", items[0].SourceId);
    }

    [Fact]
    public async Task Qq_play_without_a_picker_reports_why_it_cannot_play()
    {
        var p = new QqMusicPlatform();

        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", SourceId = "0039MnYb0qxYhV" }));
        Assert.NotNull(p.LastFailureReason);
        Assert.Contains("没有可用的点歌通道", p.LastFailureReason!);
        Assert.Equal(PlatformCapability.SearchOnly, p.Capability);
    }

    [Fact]
    public async Task Qq_play_succeeds_only_when_the_client_really_plays_the_song()
    {
        var picker = new FakePicker(PickOutcome.Started());
        var probe = new FakeProbe(new PlaybackSnapshot("晴天", "周杰伦", "playing", "QQMusic.exe"));
        var p = new QqMusicPlatform { Picker = picker, Probe = probe };

        Assert.True(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", SourceId = "0039MnYb0qxYhV" }));
        Assert.Equal("晴天 周杰伦", picker.LastQuery);
        Assert.Equal(PlatformCapability.SearchAndPlay, p.Capability);
    }

    [Fact]
    public async Task Qq_play_fails_when_the_client_plays_something_else()
    {
        // 点了但放的不是那首 —— 必须报失败，不能"看起来成功"
        var picker = new FakePicker(PickOutcome.Started());
        var probe = new FakeProbe(new PlaybackSnapshot("稻香", "周杰伦", "playing", "QQMusic.exe"));
        var p = new QqMusicPlatform { Picker = picker, Probe = probe };

        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", SourceId = "0039MnYb0qxYhV" }));
        Assert.Contains("没有在放这首歌", p.LastFailureReason!);
    }

    [Fact]
    public async Task Qq_play_fails_when_nothing_is_playing_at_all()
    {
        var picker = new FakePicker(PickOutcome.Started());
        var probe = new FakeProbe(null);
        var p = new QqMusicPlatform { Picker = picker, Probe = probe };

        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", SourceId = "0039MnYb0qxYhV" }));
        Assert.NotNull(p.LastFailureReason);
    }

    [Fact]
    public async Task Qq_play_passes_through_the_pickers_own_reason()
    {
        var picker = new FakePicker(new PickOutcome(PickStatus.NotRunning, "QQ音乐 没有找到可交互的主窗口"));
        var p = new QqMusicPlatform { Picker = picker, Probe = new FakeProbe(null) };

        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", SourceId = "0039MnYb0qxYhV" }));
        Assert.Equal("QQ音乐 没有找到可交互的主窗口", p.LastFailureReason);
    }

    // ------------------------------------------------------------ 酷狗

    private const string KugouJson = """
    {"data":{"info":[
      {"hash":"8DDF9F1EF57C0D3F3E5A0D1C2B3A4E5F","songname_original":"晴天",
       "singername":"周杰伦","album_name":"叶惠美","duration":269},
      {"songname_original":"没有 hash 的脏数据","singername":"某人","duration":100}
    ]}}
    """;

    [Fact]
    public void Kugou_search_uses_hash_as_id()
    {
        var items = KugouPlatform.Parser.Parse(KugouJson, 10);

        Assert.Single(items);
        var k = items[0];
        Assert.Equal("晴天", k.Title);
        Assert.Equal("周杰伦", k.Artist);
        Assert.Equal("kugou", k.Source);
        Assert.Equal("8DDF9F1EF57C0D3F3E5A0D1C2B3A4E5F", k.SourceId);
        Assert.Equal(269.0, k.Dur!.Value, 1e-6);
    }

    [Fact]
    public async Task Kugou_play_without_a_picker_or_template_reports_why()
    {
        var p = new KugouPlatform();

        Assert.False(await p.PlayAsync(new SearchResultItem { SourceId = "ABC123" }));
        Assert.Contains("没有可用的点歌通道", p.LastFailureReason!);
        Assert.Equal(PlatformCapability.SearchOnly, p.Capability);
    }

    [Fact]
    public async Task Kugou_play_uses_a_user_provided_template_when_present()
    {
        var p = new KugouPlatform { PlayTemplate = "kugou://song/hash/{id}" };

        // 模板是用户自己验证过的 → 允许使用
        Assert.True(await p.PlayAsync(new SearchResultItem { SourceId = "ABC123" }));
        Assert.Equal(PlatformCapability.SearchAndPlay, p.Capability);
    }

    [Fact]
    public async Task Kugou_play_works_with_a_picker_and_verified_playback()
    {
        var picker = new FakePicker(PickOutcome.Started());
        var probe = new FakeProbe(new PlaybackSnapshot("晴天", "周杰伦", "playing", "kugou"));
        var p = new KugouPlatform { Picker = picker, Probe = probe };

        Assert.True(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", SourceId = "ABC123" }));
    }

    // ------------------------------------------------------------ 酷我

    [Fact]
    public async Task Kuwo_no_search_provider_reports_honestly_and_is_play_only()
    {
        var p = new KuwoPlatform();
        var items = await p.SearchAsync("晴天 周杰伦", 10);
        Assert.Empty(items);
        Assert.Contains("搜索", p.LastFailureReason!);
        Assert.Equal(PlatformCapability.None, p.Capability);   // 无 Picker 也无搜索源
    }

    [Fact]
    public async Task Kuwo_play_without_picker_reports_why()
    {
        var p = new KuwoPlatform { SearchProvider = _kuwoSearchProvider };
        Assert.Equal(PlatformCapability.SearchOnly, p.Capability);
        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "kuwo" }));
        Assert.Contains("没有可用的点歌通道", p.LastFailureReason!);
    }

    [Fact]
    public async Task Kuwo_play_works_with_picker_and_verified_playback()
    {
        var picker = new FakePicker(PickOutcome.Started());
        var probe = new FakeProbe(new PlaybackSnapshot("晴天", "周杰伦", "playing", "kuwo"));
        var p = new KuwoPlatform { Picker = picker, Probe = probe, SearchProvider = _kuwoSearchProvider };

        Assert.True(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "kuwo" }));
        Assert.Equal("晴天 周杰伦", picker.LastQuery);
        Assert.Equal(PlatformCapability.SearchAndPlay, p.Capability);
    }

    [Fact]
    public async Task Kuwo_search_uses_injected_provider()
    {
        var p = new KuwoPlatform { SearchProvider = _kuwoSearchProvider };
        var items = await p.SearchAsync("晴天", 5);
        Assert.NotEmpty(items);
        Assert.Equal("kuwo", items[0].Source);
    }

    private static Task<IReadOnlyList<SearchResultItem>> _kuwoSearchProvider(string kw, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SearchResultItem>>(
            new List<SearchResultItem> { new() { Title = "晴天", Artist = "周杰伦", Source = "kuwo", SourceId = "rid-1" } });

    // ------------------------------------------------------------ 汽水

    [Fact]
    public async Task Qishui_no_search_provider_reports_honestly_and_is_play_only()
    {
        var p = new QishuiPlatform();
        var items = await p.SearchAsync("晴天 周杰伦", 10);
        Assert.Empty(items);
        Assert.Contains("搜索", p.LastFailureReason!);
        Assert.Equal(PlatformCapability.None, p.Capability);
    }

    [Fact]
    public async Task Qishui_play_without_picker_reports_why()
    {
        var p = new QishuiPlatform();
        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "qishui" }));
        Assert.Contains("没有可用的点歌通道", p.LastFailureReason!);
    }

    [Fact]
    public async Task Qishui_play_with_picker_but_no_probe_reports_cannot_verify()
    {
        // 汽水桌面版未确证注册 SMTC 会话 → 探针为 null；点了但没法确认真在播，必须如实报
        var picker = new FakePicker(PickOutcome.Started());
        var p = new QishuiPlatform { Picker = picker, Probe = null };

        Assert.False(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "qishui" }));
        Assert.Contains("无法确认", p.LastFailureReason!);
        Assert.Equal(PlatformCapability.PlayOnly, p.Capability);
    }

    [Fact]
    public async Task Qishui_play_succeeds_with_picker_and_verified_playback()
    {
        var picker = new FakePicker(PickOutcome.Started());
        var probe = new FakeProbe(new PlaybackSnapshot("晴天", "周杰伦", "playing", "qishui"));
        var p = new QishuiPlatform { Picker = picker, Probe = probe };

        Assert.True(await p.PlayAsync(new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "qishui" }));
        Assert.Equal(PlatformCapability.PlayOnly, p.Capability);   // 能点、没搜索源
    }

    // ------------------------------------------------------------ 解析健壮性

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"data":{"song":{}}}""")]
    [InlineData("""{"data":{"info":{}}}""")]
    public void All_platforms_return_empty_on_garbage_instead_of_throwing(string json)
    {
        Assert.Empty(NeteasePlatform.NeteaseParser.Parse(json, 10));
        Assert.Empty(QqMusicPlatform.Parser.Parse(json, 10));
        Assert.Empty(KugouPlatform.Parser.Parse(json, 10));
    }

    [Fact]
    public void Parsers_respect_the_limit()
    {
        Assert.Single(NeteasePlatform.NeteaseParser.Parse(NeteaseJson, 1));
        Assert.Single(QqMusicPlatform.Parser.Parse(QqJson, 1));
        Assert.Single(KugouPlatform.Parser.Parse(KugouJson, 1));
    }

    [Fact]
    public void Norm_normalizes_case_punctuation_and_spacing()
    {
        Assert.Equal(HttpJson.Norm("Don't Stop Believin'"), HttpJson.Norm("dont stop believin"));
    }

    // ------------------------------------------------------------ 测试替身

    private sealed class FakePicker(PickOutcome outcome) : IMusicPicker
    {
        public string AppId => "fake";
        public string? LastQuery { get; private set; }

        public Task<PickOutcome> PickAsync(string title, string artist, IPlaybackProbe? probe = null, CancellationToken ct = default)
        {
            LastQuery = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeProbe(PlaybackSnapshot? snapshot) : IPlaybackProbe
    {
        public Task<PlaybackSnapshot?> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(snapshot);
    }
}
