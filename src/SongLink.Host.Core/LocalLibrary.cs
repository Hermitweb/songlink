using System.Text.Json;

namespace SongLink.Host;

/// <summary>
/// 本地音源库（架构 §3.6 import / 契约 §3.2 import）：登记导入的曲目（元数据 + 落盘文件），供检索。
/// 实际播放由 LocalSource 适配器驱动（真机项）。线程安全、可本机测。
/// </summary>
public sealed class LocalLibrary
{
    public sealed record Track(string Title, string Artist, string File);

    private readonly string _dir;
    private readonly List<Track> _tracks = new();
    private readonly object _lock = new();

    public LocalLibrary(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "library");
        Directory.CreateDirectory(_dir);
        LoadIndex();
    }

    private string IndexPath => Path.Combine(_dir, "index.json");

    private void LoadIndex()
    {
        if (!File.Exists(IndexPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(IndexPath));
            if (list is not null) lock (_lock) _tracks.AddRange(list);
        }
        catch { /* 索引损坏则忽略，从空开始 */ }
    }

    private void SaveIndex()
    {
        List<Track> snap; lock (_lock) snap = _tracks.ToList();
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(snap));
    }

    private static string SafeName(string s) =>
        new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();

    /// <summary>导入一首：落盘音频字节（可空则仅登记元数据）+ 加入索引。返回登记后的曲目。</summary>
    public Track Import(string title, string artist, byte[]? audio, string ext = ".mp3")
    {
        title = (title ?? "").Trim();
        if (string.IsNullOrEmpty(title)) throw new ArgumentException("标题不能为空");
        var file = $"{SafeName(title)} - {SafeName(artist)}{ext}";
        if (audio is { Length: > 0 })
            File.WriteAllBytes(Path.Combine(_dir, file), audio);
        var track = new Track(title, (artist ?? "").Trim(), file);
        lock (_lock) _tracks.Add(track);
        SaveIndex();
        return track;
    }

    /// <summary>按关键词（标题/艺人不区分大小写子串）检索。</summary>
    public List<Track> Search(string kw)
    {
        kw = (kw ?? "").Trim();
        if (string.IsNullOrEmpty(kw)) return new List<Track>();
        lock (_lock)
            return _tracks.Where(t =>
                t.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public int Count { get { lock (_lock) return _tracks.Count; } }
}
