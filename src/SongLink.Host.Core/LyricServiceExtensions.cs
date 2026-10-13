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

    /// <summary>转换 LRCLib JSON → LRC 文本格式</summary>
    private static string ConvertLrclibToLrc(LrclibLyrics lrclib)
    {
        var lines = new List<string>();

        if (lrclib.syncedLyrics is null) return string.Empty;

        foreach (var line in lrclib.syncedLyrics.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 2)
            {
                var timeMs = double.Parse(parts[0]);
                var text = string.Join(" ", parts.Skip(1));
                
                var min = (int)(timeMs / 60000);
                var sec = (int)((timeMs % 60000) / 1000);
                var ms = (int)(timeMs % 1000);
                
                // [mm:ss.xx] 歌词文本
                lines.Add($"[{min:D2}:{sec:D2}.{ms:D2}] {text.Trim()}");
            }
        }
        
        // 添加标题元数据
        lines.Insert(0, $"[ti:{lrclib.track?.title}]");
        lines.Insert(1, $"[ar:{lrclib.track?.artist}]");
        
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
