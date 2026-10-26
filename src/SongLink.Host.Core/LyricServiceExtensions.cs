using System.Net.Http.Json;
using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 歌词服务增强（架构 §6.4 / S15）：LRCLib API 联网拉取 + 缓存策略
/// 优先级：本地 LRC > 本地 KRC > LRCLib 在线 > 静态 fallback
/// </summary>
public static class LyricServiceExtensions
{
    /// <summary>LRCLib API 客户端封装</summary>
    private static readonly HttpClient LrclibClient = new()
    {
        BaseAddress = new Uri("https://lrclib.net/api/")
    };

    /// <summary>尝试从 LRCLib 获取同步歌词（带中文搜索优化）</summary>
    public static async Task<bool> TryFetchLyricAsync(
        this LyricService service, 
        string title, 
        string artist, 
        CancellationToken ct = default)
    {
        if (service.TryGet(title, artist) is not null)
            return true; // 本地已有，跳过

        try
        {
            // LRCLib 要求 title 与 artist **分开**传（此前把 "{title} {artist}" 整串同时
            // 塞进两个参数，带歌手的查询基本永远 404/miss）。
            var uri = new UriBuilder("get")
            {
                Query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}"
            }.Uri;
            var response = await LrclibClient.GetAsync(uri, ct);

            if (!response.IsSuccessStatusCode) return false;

            var lyricsJson = await response.Content.ReadAsStringAsync(ct);
            var lrclibResponse = System.Text.Json.JsonSerializer.Deserialize<LrclibLyrics>(lyricsJson);

            var synced = lrclibResponse?.syncedLyrics;
            // 只要带时间轴的同步歌词：纯文本(plainLyrics)没有时间戳，
            // 缓存进来 LyricService 也解析不出时间轴（TryGet 仍返回 null），
            // 且会污染缓存目录、让后续“本地已有”判断误判，故不缓存。
            if (string.IsNullOrWhiteSpace(synced)) return false;

            // 转换为本地 LRC 格式并缓存（有 duration 时补 [length:]，供 #11 唇同步/进度对齐）
            var lrcContent = ConvertLrclibToLrc(lrclibResponse!);
            var filePath = Path.Combine(service.LibraryDirectory, $"{SafeName(title)} - {SafeName(artist)}.lrc");

            File.WriteAllText(filePath, lrcContent);
            Console.WriteLine($"[lyric] 成功从 LRCLib 拉取并缓存：{title} - {artist}");

            return true;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[lyric] LRCLib API 请求失败：{ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lyric] 未知错误：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 去掉文件名非法字符。与 LyricService.Key / LocalLibrary.SafeName 同一套规则
    /// （实际统一委托到 <see cref="PathSafeName.Sanitize"/>），保证缓存文件名和
    /// TryGet 的查找路径一致（否则拉回来的歌词永远读不到）。
    /// </summary>
    private static string SafeName(string s) => PathSafeName.Sanitize(s);

    /// <summary>
    /// LRCLib JSON → 本地 LRC 文本。
    /// LRCLib 的 <c>syncedLyrics</c> 字段本身就是 LRC 格式（每行 <c>[mm:ss.xx]text</c>），
    /// 旧实现把它按"空格切分再 double.Parse 时间"是错误前提（实际上第 0 段是
    /// <c>[01:23.45]</c> 这种带方括号带冒号的字符串，<c>double.Parse</c> 必抛 FormatException）。
    /// 正确做法：直接透传 <c>syncedLyrics</c>，仅当缺元数据时按 LRC 惯例在头部补
    /// <c>[ti:]</c> 与 <c>[ar:]</c>；有 <c>duration</c> 时再补 <c>[length:]</c>（#11 进度对齐用）。
    /// </summary>
    private static string ConvertLrclibToLrc(LrclibLyrics lrclib)
    {
        var body = lrclib.syncedLyrics ?? string.Empty;
        if (body.Length == 0) return string.Empty;

        var lines = body.Replace("\r\n", "\n").Split('\n').ToList();

        bool hasTi = lines.Any(l => l.TrimStart().StartsWith("[ti:", StringComparison.OrdinalIgnoreCase));
        bool hasAr = lines.Any(l => l.TrimStart().StartsWith("[ar:", StringComparison.OrdinalIgnoreCase));
        bool hasLength = lines.Any(l => l.TrimStart().StartsWith("[length:", StringComparison.OrdinalIgnoreCase));

        var head = new List<string>();
        if (!hasTi && lrclib.track?.title is { Length: > 0 } tt)
            head.Add($"[ti:{tt}]");
        if (!hasAr && lrclib.track?.artist is { Length: > 0 } ar)
            head.Add($"[ar:{ar}]");
        if (!hasLength && lrclib.track?.duration is { Length: > 0 } dur)
            head.Add($"[length:{dur}]");
        lines.InsertRange(0, head);

        return string.Join("\n", lines);
    }

    /// <summary>
    /// LRCLib API 响应模型。字段对齐 https://lrclib.net/api-docs：
    /// <see href="https://lrclib.net/api-docs#/default/get-synced-lyrics-by-track-details/get_0"/>。
    /// 同步歌词 = <c>syncedLyrics</c>；纯文本 = <c>plainLyrics</c>（无时间轴，不缓存）；
    /// 时长 = <c>track.duration</c>（"MM:SS"）。
    /// </summary>
    private sealed class LrclibLyrics
    {
        public TrackInfo? track { get; set; }
        public string? syncedLyrics { get; set; }
        public string? plainLyrics { get; set; }
    }

    private sealed class TrackInfo
    {
        public string? title { get; set; }
        public string? artist { get; set; }
        public string? album { get; set; }

        /// <summary>时长 "MM:SS"（LRCLib 返回字符串）。</summary>
        public string? duration { get; set; }
    }
}
