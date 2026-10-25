using SongLink.Host;
using SongLink.Host.DeepLink;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// 「接管正在播放」模式适配器的行为测试。
///
/// 这组用例钉住三件事，它们直接对应用户能不能真的点歌：
///   1. 搜索 = 本地曲库 + 各在线平台聚合，且每条都带 source/sourceId（点歌路由的依据）；
///   2. 点歌按 source 路由到对应平台，**用搜索时确定的 ID 直接播**，不做二次名称匹配；
///   3. 平台不可用/没配 → 如实返回 false 并退回本地，绝不假装成功。
/// </summary>
public class TakeoverAdapterTests
{
    private sealed class FakeCatalogue : IPlayerAdapter
    {
        public string AppId => "fake-catalogue";
        public AdapterCapability Caps => AdapterCapability.Search | AdapterCapability.Pick;
        public List<string> Searched { get; } = new();
        public List<string> Picked { get; } = new();

        public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default) => Task.FromResult<NowPlaying?>(null);
        public Task<bool> Play(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Pause(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Next(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Prev(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Stop(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> Pick(string t, string a, CancellationToken ct = default)
        {
            Picked.Add($"{t}|{a}");
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
        {
            Searched.Add(kw);
            IReadOnlyList<SearchResultItem> results = new[]
            {
                new SearchResultItem { Title = "本地晴天", Artist = "周杰伦", SourceId = "local-file-1" },
            };
            return Task.FromResult(results);
        }

        public Task<double> GetVolume(CancellationToken ct = default) => Task.FromResult(1.0);
        public Task<bool> SetVolume(double v, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeTransport : IPlayerAdapter
    {
        public string AppId => "fake-smtc";
        public AdapterCapability Caps => AdapterCapability.ReadMeta | AdapterCapability.Transport;
        public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default)
            => Task.FromResult<NowPlaying?>(new NowPlaying { Title = "正在放的歌", Artist = "某人" });
        public Task<bool> Play(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Pause(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Next(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Prev(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Stop(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Pick(string t, string a, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        public Task<double> GetVolume(CancellationToken ct = default) => Task.FromResult(1.0);
        public Task<bool> SetVolume(double v, CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>可编程的假在线平台。</summary>
    private sealed class FakePlatform(string kind, SearchResultItem[] results, bool playResult = true) : IMusicPlatform
    {
        public List<string> Played { get; } = new();
        public string Kind => kind;
        public string Name => kind;
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string kw, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResultItem>>(results);
        public Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
        {
            Played.Add($"{item.SourceId}");
            return Task.FromResult(playResult);
        }
    }

    // -------------------------------------------------------------- 搜索聚合

    [Fact]
    public async Task Search_merges_local_and_online_results_all_tagged_with_source()
    {
        var catalogue = new FakeCatalogue();
        var qq = new FakePlatform("qq", new[]
        {
            new SearchResultItem { Title = "在线晴天", Artist = "周杰伦", Source = "qq", SourceId = "97773" },
        });
        var sut = new TakeoverAdapter(new FakeTransport(), catalogue, new IMusicPlatform[] { qq });

        var results = await sut.Search("晴天");

        Assert.Contains("晴天", catalogue.Searched);
        Assert.Equal(2, results.Count);
        // 本地结果也要标 source，否则点歌时路由不了
        Assert.Equal("local", results.Single(r => r.Title == "本地晴天").Source);
        Assert.Equal("97773", results.Single(r => r.Title == "在线晴天").SourceId);
    }

    [Fact]
    public async Task Search_still_works_when_a_platform_throws()
    {
        var catalogue = new FakeCatalogue();
        var bad = new ThrowingPlatform();
        var good = new FakePlatform("netease", new[]
        {
            new SearchResultItem { Title = "活下来了", Artist = "某人", Source = "netease", SourceId = "1" },
        });
        var sut = new TakeoverAdapter(new FakeTransport(), catalogue, new IMusicPlatform[] { bad, good });

        var results = await sut.Search("歌");

        // 一个平台炸了不能影响其它源
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Title == "活下来了");
    }

    private sealed class ThrowingPlatform : IMusicPlatform
    {
        public string Kind => "bad";
        public string Name => "bad";
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string kw, int limit, CancellationToken ct = default)
            => throw new InvalidOperationException("接口挂了");
        public Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default) => Task.FromResult(false);
    }

    // -------------------------------------------------------------- 点歌路由

    [Fact]
    public async Task Pick_routes_to_the_platform_the_song_came_from()
    {
        var catalogue = new FakeCatalogue();
        var qq = new FakePlatform("qq", new[]
        {
            new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "qq", SourceId = "97773" },
        });
        var sut = new TakeoverAdapter(new FakeTransport(), catalogue, new IMusicPlatform[] { qq });

        await sut.Search("晴天");
        Assert.True(await sut.Pick("晴天", "周杰伦"));

        // 用的是搜索时拿到的 ID，而不是再按歌名去查一遍
        Assert.Equal(new[] { "97773" }, qq.Played);
        Assert.Empty(catalogue.Picked);
    }

    [Fact]
    public async Task Pick_matches_ignoring_case_and_punctuation()
    {
        var catalogue = new FakeCatalogue();
        var netease = new FakePlatform("netease", new[]
        {
            new SearchResultItem { Title = "Don't Stop", Artist = "Journey", Source = "netease", SourceId = "186016" },
        });
        var sut = new TakeoverAdapter(new FakeTransport(), catalogue, new IMusicPlatform[] { netease });

        await sut.Search("dont stop");
        Assert.True(await sut.Pick("Don't Stop", "Journey"));
        Assert.Equal(new[] { "186016" }, netease.Played);
    }

    [Fact]
    public async Task Pick_falls_back_to_local_when_platform_cannot_play()
    {
        var catalogue = new FakeCatalogue();
        var qq = new FakePlatform("qq", new[]
        {
            new SearchResultItem { Title = "晴天", Artist = "周杰伦", Source = "qq", SourceId = "97773" },
        }, playResult: false);
        var sut = new TakeoverAdapter(new FakeTransport(), catalogue, new IMusicPlatform[] { qq });

        await sut.Search("晴天");

        // 平台没装 / 协议失效 → 退回本地，而不是什么都不做
        Assert.True(await sut.Pick("晴天", "周杰伦"));
        Assert.Equal(new[] { "晴天|周杰伦" }, catalogue.Picked);
    }

    [Fact]
    public async Task Pick_of_an_never_searched_song_goes_to_local()
    {
        var catalogue = new FakeCatalogue();
        var sut = new TakeoverAdapter(new FakeTransport(), catalogue, Array.Empty<IMusicPlatform>());

        Assert.True(await sut.Pick("从没搜过的歌", "某人"));
        Assert.Equal(new[] { "从没搜过的歌|某人" }, catalogue.Picked);
    }

    [Fact]
    public async Task Pick_fails_honestly_when_nothing_can_play_it()
    {
        var failing = new FailingCatalogue();
        var sut = new TakeoverAdapter(new FakeTransport(), failing, Array.Empty<IMusicPlatform>());

        Assert.False(await sut.Pick("不存在的歌", "某人"));
    }

    private sealed class FailingCatalogue : IPlayerAdapter
    {
        public string AppId => "empty";
        public AdapterCapability Caps => AdapterCapability.Search;
        public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default) => Task.FromResult<NowPlaying?>(null);
        public Task<bool> Play(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Pause(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Next(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Prev(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Stop(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> Pick(string t, string a, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());
        public Task<double> GetVolume(CancellationToken ct = default) => Task.FromResult(1.0);
        public Task<bool> SetVolume(double v, CancellationToken ct = default) => Task.FromResult(true);
    }

    // -------------------------------------------------------------- 传输控制

    [Fact]
    public async Task Metadata_and_transport_come_from_smtc()
    {
        var sut = new TakeoverAdapter(new FakeTransport(), new FakeCatalogue(), Array.Empty<IMusicPlatform>());

        Assert.Equal("正在放的歌", (await sut.ReadNowPlaying())!.Title);
        Assert.True(await sut.Play());
        Assert.True(await sut.Next());
        Assert.True(await sut.Prev());
        Assert.True(sut.Caps.HasFlag(AdapterCapability.Transport));
        Assert.True(sut.Caps.HasFlag(AdapterCapability.Search));
    }

    // -------------------------------------------------------------- 配置闸门

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"songlink_takeover_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Missing_config_yields_no_online_platforms()
    {
        Assert.Empty(PlatformConfigLoader.Load(Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void Config_without_risk_acknowledgement_is_ignored()
    {
        var path = WriteTemp("""
        {"acknowledgeRisk": false, "platforms": [{"kind":"netease","enabled":true}]}
        """);
        try { Assert.Empty(PlatformConfigLoader.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Config_enables_the_listed_platforms()
    {
        var path = WriteTemp("""
        { "acknowledgeRisk": true,
          "platforms": [
            {"kind":"netease","enabled":true},
            {"kind":"qq","enabled":true},
            {"kind":"kugou","enabled":true,"playTemplate":"kugou://x/{id}"}
          ] }
        """);
        try
        {
            var platforms = PlatformConfigLoader.Load(path);
            Assert.Equal(3, platforms.Count);
            Assert.Equal(new[] { "netease", "qq", "kugou" }, platforms.Select(p => p.Kind));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Disabled_and_unknown_platforms_are_skipped()
    {
        var path = WriteTemp("""
        { "acknowledgeRisk": true,
          "platforms": [
            {"kind":"netease","enabled":true},
            {"kind":"qq","enabled":false},
            {"kind":"spotify","enabled":true},
            {"kind":"","enabled":true}
          ] }
        """);
        try { Assert.Single(PlatformConfigLoader.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Malformed_config_degrades_gracefully()
    {
        var path = WriteTemp("{ broken json");
        try { Assert.Empty(PlatformConfigLoader.Load(path)); }
        finally { File.Delete(path); }
    }
}
