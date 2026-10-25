using SongLink.Host.DeepLink;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// 搜索结果排序测试。
///
/// 用的是**真实抓取**的网易云「晴天 周杰伦」搜索响应 —— 平台返回的前 5 条
/// 全是改编，周杰伦原版（id 186016）根本不在其中。这组用例钉住排序必须能纠正它。
/// </summary>
public class ResultRankerTests
{
    /// <summary>网易云搜「晴天 周杰伦」的原始返回（原样保留，含顺序）。</summary>
    private const string RealNeteaseJson = """
    {"result":{"songs":[
      {"id":2652820720,"name":"晴天(深情版)","artists":[{"name":"Lucky小爱"}]},
      {"id":3440441479,"name":"晴天","artists":[{"name":"Jay"}]},
      {"id":2668397359,"name":"晴天 (原唱 周杰伦)","artists":[{"name":"RyaVocal"}]},
      {"id":2653714443,"name":"晴天 (女声版))","artists":[{"name":"GYBeat"}]},
      {"id":1945894789,"name":"晴天 (钢琴版) [原唱: 周杰伦]","artists":[{"name":"纪钧瀚"}]},
      {"id":186016,"name":"晴天","artists":[{"name":"周杰伦"}],"album":{"name":"叶惠美"},"duration":269000}
    ]}}
    """;

    [Fact]
    public void Real_world_case_original_song_rises_to_the_top()
    {
        var raw = NeteasePlatform.NeteaseParser.Parse(RealNeteaseJson, 10);

        // 平台原始返回：原版排在第 6 位（最后）
        Assert.Equal("186016", raw[^1].SourceId);

        var ranked = ResultRanker.Rank(raw, "晴天 周杰伦");

        // 排序后必须排第一 —— 否则用户点第一条放出来的是翻唱
        Assert.Equal("186016", ranked[0].SourceId);
        Assert.Equal("晴天", ranked[0].Title);
        Assert.Equal("周杰伦", ranked[0].Artist);
    }

    [Fact]
    public void Cover_versions_are_pushed_behind_the_original()
    {
        var raw = NeteasePlatform.NeteaseParser.Parse(RealNeteaseJson, 10);
        var ranked = ResultRanker.Rank(raw, "晴天 周杰伦");

        var originalIndex = ranked.ToList().FindIndex(x => x.SourceId == "186016");
        foreach (var coverId in new[] { "2652820720", "2653714443", "1945894789" })
        {
            var i = ranked.ToList().FindIndex(x => x.SourceId == coverId);
            Assert.True(i > originalIndex, $"cover {coverId} should rank after the original");
        }
    }

    [Fact]
    public void No_rewrite_of_results_only_reordering()
    {
        var raw = NeteasePlatform.NeteaseParser.Parse(RealNeteaseJson, 10);
        var ranked = ResultRanker.Rank(raw, "晴天 周杰伦");

        // 条数不变、内容不变，只是换了顺序
        Assert.Equal(raw.Count, ranked.Count);
        Assert.Equal(
            raw.Select(x => x.SourceId).OrderBy(x => x),
            ranked.Select(x => x.SourceId).OrderBy(x => x));
    }

    // ------------------------------------------------------------ 打分细节

    [Fact]
    public void Exact_title_and_artist_beats_title_only()
    {
        var exact = new SearchResultItem { Title = "晴天", Artist = "周杰伦" };
        var titleOnly = new SearchResultItem { Title = "晴天", Artist = "某某" };

        Assert.True(
            ResultRanker.Score(exact, HttpJson.Norm("晴天 周杰伦")) >
            ResultRanker.Score(titleOnly, HttpJson.Norm("晴天 周杰伦")));
    }

    [Fact]
    public void Artist_matching_the_query_is_rewarded()
    {
        var withArtist = new SearchResultItem { Title = "稻香", Artist = "周杰伦" };
        var otherArtist = new SearchResultItem { Title = "稻香", Artist = "林俊杰" };

        Assert.True(
            ResultRanker.Score(withArtist, HttpJson.Norm("稻香 周杰伦")) >
            ResultRanker.Score(otherArtist, HttpJson.Norm("稻香 周杰伦")));
    }

    [Theory]
    [InlineData("晴天(深情版)", true)]
    [InlineData("晴天 (女声版)", true)]
    [InlineData("晴天 (钢琴版)", true)]
    [InlineData("晴天 (原唱 周杰伦)", true)]
    [InlineData("Sunny Day (Piano Version)", true)]
    [InlineData("晴天", false)]
    [InlineData("稻香", false)]
    [InlineData("Don't Stop Believin'", false)]
    public void Cover_markers_are_detected(string title, bool expected)
    {
        Assert.Equal(expected, ResultRanker.HasCoverMarker(title));
    }

    [Fact]
    public void Ranking_is_stable_for_equal_scores()
    {
        // 三条同分结果应保持平台返回的原顺序，不被随机打乱
        var list = new List<SearchResultItem>
        {
            new() { Title = "A", Artist = "X" },
            new() { Title = "B", Artist = "Y" },
            new() { Title = "C", Artist = "Z" },
        };

        var ranked = ResultRanker.Rank(list, null);

        Assert.Equal(new[] { "A", "B", "C" }, ranked.Select(x => x.Title));
    }

    [Fact]
    public void Empty_and_single_results_pass_through()
    {
        Assert.Empty(ResultRanker.Rank(Array.Empty<SearchResultItem>(), "歌"));
        var one = new List<SearchResultItem> { new() { Title = "晴天" } };
        Assert.Single(ResultRanker.Rank(one, "晴天"));
    }

    [Fact]
    public void Null_titled_results_sink()
    {
        var list = new List<SearchResultItem>
        {
            new() { Title = "" },
            new() { Title = "晴天", Artist = "周杰伦" },
        };
        var ranked = ResultRanker.Rank(list, "晴天");
        Assert.Equal("晴天", ranked[0].Title);
    }
}
