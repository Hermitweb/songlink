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
        
        var query = $"{title} {artist}".Replace(' ', '+');
        
        try
        {
            // 调用 LRCLib GET /get?track_name=xxx&artist_name=xxx
            var response = await LrclibClient.GetAsync(
                $"get?track_name={query}&artist_name={query}", 
                ct);
            
            if (!response.IsSuccessStatusCode) return false;
            
            var lyricsJson = await response.Content.ReadAsStringAsync(ct);
            var lrclibResponse = System.Text.Json.JsonSerializer.Deserialize<LrclibLyrics>(lyricsJson);
            
            if (lrclibResponse?.syncedLyrics is not null && lrclibResponse.syncedLyrics.Length > 0)
            {
                // 转换为本地 LRC 格式并缓存
                var lrcContent = ConvertLrclibToLrc(lrclibResponse);
                var filePath = Path.Combine(service.LibraryDirectory, $"{SafeName(title)} - {SafeName(artist)}.lrc");
                
                File.WriteAllText(filePath, lrcContent);
                Console.WriteLine($"[lyric] 成功从 LRCLib 拉取并缓存：{title} - {artist}");
                
                return true;
            }
            
            return false;
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
    /// 去掉文件名非法字符。与 LyricService.Key / LocalLibrary.SafeName 同一套规则，
    /// 保证缓存文件名和 TryGet 的查找路径一致（否则拉回来的歌词永远读不到）。
    /// </summary>
    private static string SafeName(string s) =>
        new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();

    /// <summary>
    /// LRCLib JSON → 本地 LRC 文本。
    /// LRCLib 的 <c>syncedLyrics</c> 字段本身就是 LRC 格式（每行 <c>[mm:ss.xx]text</c>），
    /// 旧实现把它按"空格切分再 double.Parse 时间"是错误前提（实际上第 0 段是
    /// <c>[01:23.45]</c> 这种带方括号带冒号的字符串，<c>double.Parse</c> 必抛 FormatException）。
    /// 正确做法：直接透传 <c>syncedLyrics</c>，仅当缺元数据时按 LRC 惯例在头部补
    /// <c>[ti:]</c> 与 <c>[ar:]</c>。
    /// </summary>
    private static string ConvertLrclibToLrc(LrclibLyrics lrclib)
    {
        var body = lrclib.syncedLyrics ?? string.Empty;
        if (body.Length == 0) return string.Empty;

        var lines = body.Replace("\r\n", "\n").Split('\n').ToList();

        bool hasTi = lines.Any(l => l.TrimStart().StartsWith("[ti:", StringComparison.OrdinalIgnoreCase));
        bool hasAr = lines.Any(l => l.TrimStart().StartsWith("[ar:", StringComparison.OrdinalIgnoreCase));

        if (!hasTi && lrclib.track?.title is { Length: > 0 } tt)
            lines.Insert(0, $"[ti:{tt}]");
        if (!hasAr && lrclib.track?.artist is { Length: > 0 } ar)
        {
            // [ar:] 应紧随 [ti:] 之后；若 [ti:] 是新插入的，它在 index 0，
            // 否则 [ti:] 是 LRCLib 原本就有的，仍在 index 0。
            int insertAt = (lines.Count > 0 && lines[0].TrimStart().StartsWith("[ti:", StringComparison.OrdinalIgnoreCase))
                ? 1 : 0;
            lines.Insert(insertAt, $"[ar:{ar}]");
        }

        return string.Join("\n", lines);
    }

    /// <summary>LRCLib API 响应模型</summary>
    private sealed class LrclibLyrics
    {
        public TrackInfo? track { get; set; }
        public string? syncedLyrics { get; set; }
    }

    private sealed class TrackInfo
    {
        public string? title { get; set; }
        public string? artist { get; set; }
        public string? album { get; set; }
    }
}
