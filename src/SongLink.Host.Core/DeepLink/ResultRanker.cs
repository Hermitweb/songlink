using SongLink.Protocol;

namespace SongLink.Host.DeepLink;

/// <summary>
/// 搜索结果排序。
///
/// <para><b>为什么需要它</b>：各平台的搜索是"关键词模糊匹配"，并不理解用户想听的是原版。
/// 实测网易云搜「晴天 周杰伦」前 5 条全是改编（深情版 / 女声版 / 钢琴版 / 原唱翻唱），
/// **周杰伦原版根本不在前 5 条**。用户点第一条，放出来的就是一首翻唱。
/// 搜索结果自带 ID 后我们不再二次匹配（那样会放错歌），但如果不做排序，用户点的
/// 第一条本身就是错的 —— 所以排序这层必须补上。</para>
///
/// <para><b>策略</b>：不动各平台返回的内容，只调整顺序。三类信号：
/// ① 歌名精确匹配；② 歌手出现在查询里；③ 标题里带"改编"标记（深情版/钢琴版/女声版…）。
/// 同分保持原顺序（稳定排序），避免把"本来就在第一位"的准确结果挤下去。</para>
/// </summary>
public static class ResultRanker
{
    /// <summary>标题里出现这些词，多半是改编/翻唱/伴奏，而不是用户要的原版。</summary>
    /// <remarks>比较时两侧都会去掉非字母数字并转小写，所以 "Piano Version" 与 "pianoversion" 等价。</remarks>
    private static readonly string[] CoverMarkers =
    {
        // 中文
        "深情版", "女声版", "男声版", "钢琴版", "吉他版", "伴奏", "纯音乐", "原唱", "翻唱",
        "试听", "铃声", "车载", "八音盒", "钢琴演奏",
        // 英文
        "instrumental", "piano version", "guitar version", "acoustic", "karaoke",
        "cover", "remix", "live", "nightcore", "sped up", "slowed", "reprise", "demo",
    };

    /// <summary>
    /// 就地重排（返回新列表，不改入参）。
    /// </summary>
    /// <param name="results">各来源合并后的原始结果。</param>
    /// <param name="query">用户输入的原始关键词。</param>
    public static IReadOnlyList<SearchResultItem> Rank(
        IReadOnlyList<SearchResultItem> results, string? query)
    {
        if (results.Count <= 1) return results;

        var q = HttpJson.Norm(query);
        var scored = results
            .Select((r, i) => (item: r, score: Score(r, q), index: i))
            .OrderByDescending(x => x.score)      // LINQ 的 OrderBy 是稳定排序，同分保持原序
            .Select(x => x.item)
            .ToList();

        return scored;
    }

    /// <summary>给单条结果打分。分数越高越可能是用户要的。</summary>
    internal static int Score(SearchResultItem r, string normalizedQuery)
    {
        var score = 0;
        var title = HttpJson.Norm(r.Title);
        var artist = HttpJson.Norm(r.Artist);
        if (title.Length == 0) return int.MinValue / 2;   // 没歌名的直接沉底

        // ① 歌名精确命中
        if (normalizedQuery.Length > 0)
        {
            if (title == normalizedQuery) score += 100;
            else if (normalizedQuery.Contains(title)) score += 60;
            else if (title.Contains(normalizedQuery)) score += 40;
        }

        // ② 歌手出现在查询里 —— 用户多半是「歌名 歌手」这么搜的
        if (artist.Length > 0 && normalizedQuery.Contains(artist)) score += 50;
        // 歌名与歌手都没在查询里出现 → 多半是平台塞进来的相关推荐
        if (artist.Length == 0 && normalizedQuery.Length > 0) score -= 10;

        // ③ 改编/翻唱标记 —— 有"真身"在场时，这类要靠后
        if (HasCoverMarker(r.Title)) score -= 80;
        // 标题里明写"原唱 XX"而查询里的歌手不是 XX 的，本质也是翻唱
        if (r.Title.Contains("原唱") && artist.Length > 0 && !normalizedQuery.Contains(artist)) score -= 40;

        return score;
    }

    /// <summary>标题是否带改编/翻唱/伴奏类标记。</summary>
    internal static bool HasCoverMarker(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var t = HttpJson.Norm(title);
        foreach (var m in CoverMarkers)
        {
            var nm = HttpJson.Norm(m);
            if (nm.Length > 0 && t.Contains(nm)) return true;
        }
        return false;
    }
}
