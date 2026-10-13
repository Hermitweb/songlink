using System.Text.RegularExpressions;
using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 歌词服务（架构 §6.4）：按 title+artist 从本地目录加载 LRC，解析成带时间轴的 Lyric 帧。
/// 与出声方解耦——无论 PC_LOCAL 还是 PHONE_STREAM，都用同一套本地/缓存歌词。
/// 纯托管，本机可测（不依赖联网；联网拉取留后续）。
/// </summary>
public sealed class LyricService
{
    private readonly string _dir;
    private static readonly Regex Tag = new(@"\[(\d+):(\d+)(?:[.:](\d+))?\]", RegexOptions.Compiled);
    private readonly Dictionary<string, int> _offsets = new(); // trackKey -> ms（#11 唇同步）
    private int _globalOffset;
    private readonly object _offLock = new();

    public LyricService(string? dir = null)
        => _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "lyrics");

    private static string Key(string title, string artist) =>
        PathSafeName.Sanitize($"{title} - {artist}");

    /// <summary>LRC 根目录。联网拉取（LyricServiceExtensions）把新歌词缓存回这个目录。</summary>
    public string LibraryDirectory => _dir;

    private static string TrackKey(string title, string artist) => $"{title}|{artist}";

    /// <summary>设置歌词偏移（#11）。title 为空 → 设全局默认；否则设该曲偏移。</summary>
    public void SetOffset(string? title, string? artist, int ms)
    {
        lock (_offLock)
        {
            if (string.IsNullOrWhiteSpace(title)) _globalOffset = ms;
            else _offsets[TrackKey(title!, artist ?? "")] = ms;
        }
    }

    /// <summary>取某曲生效偏移：每曲优先，回落全局。</summary>
    public int GetOffset(string title, string artist)
    {
        lock (_offLock)
            return _offsets.TryGetValue(TrackKey(title, artist), out var o) ? o : _globalOffset;
    }

    /// <summary>尝试加载某曲歌词；找不到返回 null。返回帧带上该曲生效 offsetMs（#11）。</summary>
    public Lyric? TryGet(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var file = Path.Combine(_dir, Key(title, artist) + ".lrc");
        if (!File.Exists(file)) return null;
        var lines = Parse(File.ReadAllText(file));
        if (lines.Count == 0) return null;
        return new Lyric { TrackId = $"lrc:{title}-{artist}", Lines = lines, OffsetMs = GetOffset(title, artist) };
    }

    /// <summary>解析 LRC 文本为时间轴行（支持一行多标签）。</summary>
    public static List<LyricLine> Parse(string lrc)
    {
        var result = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = Tag.Matches(line);
            if (matches.Count == 0) continue;
            // 去掉所有 [..] 标签得到纯文本
            string text = Tag.Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int frac = m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(2, '0')[..2]) : 0;
                double t = min * 60 + sec + frac / 100.0;
                result.Add(new LyricLine { T = Math.Round(t, 2), X = text });
            }
        }
        return result.OrderBy(l => l.T).ToList();
    }
}
