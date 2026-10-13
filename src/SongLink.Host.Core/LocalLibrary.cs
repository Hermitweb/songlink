using System.Text.Json;
using System.Text.RegularExpressions;

namespace SongLink.Host;

/// <summary>
/// 本地音源库（架构 §3.6 import / 契约 §3.2 import）+ 落雪音乐风格增强：
/// 1. 批量扫描导入 + 2. 封面缓存 + 3. 播放历史
/// </summary>
public sealed class LocalLibrary
{
    public sealed record Track(string Title, string Artist, string File);
    
    /// <summary>播放历史（最近 50 首）</summary>
    public sealed record PlayHistory(string Title, string Artist, DateTime PlayedAt);

    private readonly string _dir;
    private readonly List<Track> _tracks = new();
    private readonly List<PlayHistory> _history = new();
    private readonly object _lock = new();

    public LocalLibrary(string? dir = null)
    {
        _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "library");
        // 全限定：本类的 Directory 属性会遮蔽 System.IO.Directory
        System.IO.Directory.CreateDirectory(_dir);
        LoadIndex();
        LoadHistory();
    }

    private string IndexPath => Path.Combine(_dir, "index.json");
    private string HistoryPath => Path.Combine(_dir, "history.json");

    /// <summary>曲库根目录（适配器按它拼音频文件绝对路径）。</summary>
    public string Directory => _dir;

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

    private void LoadHistory()
    {
        if (!File.Exists(HistoryPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<PlayHistory>>(File.ReadAllText(HistoryPath));
            if (list is not null) lock (_lock) _history.AddRange(list);
        }
        catch { /* 忽略 */ }
    }

    private void SaveHistory()
    {
        lock (_lock)
        {
            // 保留最近 50 条
            while (_history.Count > 50) _history.RemoveAt(0);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history));
        }
    }

    private static string SafeName(string s) =>
        new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();

    /// <summary>导入一首：落盘音频字节（可空则仅登记元数据）+ 加入索引。</summary>
    public Track Import(string title, string artist, byte[]? audio, string ext = ".mp3")
    {
        title = (title ?? "").Trim();
        if (string.IsNullOrEmpty(title)) throw new ArgumentException("标题不能为空");
        
        lock (_lock)
        {
            // 检查是否已存在（避免重复）
            var existing = _tracks.FirstOrDefault(t => t.Title == title && t.Artist == artist);
            if (existing is not null) return existing;
            
            var file = $"{SafeName(title)} - {SafeName(artist)}{ext}";
            if (audio is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(_dir, file), audio);
            
            var track = new Track(title, (artist ?? "").Trim(), file);
            _tracks.Add(track);
            SaveIndex();
            return track;
        }
    }

    /// <summary>批量导入：从路径列表导入多个文件。</summary>
    public async Task<ImportBatchResult> ImportBatchAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        var result = new ImportBatchResult
        {
            SuccessfulImportsList = new List<Track>()
        };
        
        foreach (var path in filePaths)
        {
            if (ct.IsCancellationRequested) break;
            
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var metadata = ParseFileName(fileName);
                
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                
                var track = Import(metadata.Title, metadata.Artist, bytes.Length > 0 ? bytes : null, Path.GetExtension(path));
                result.SuccessfulImportsList.Add(track);
                result.CurrentIndex++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[batch] 失败：{path} - {ex.Message}");
                result.FailedFiles++;
            }
        }
        
        result.TotalFiles = filePaths.Count();
        return result;
    }

    /// <summary>文件名解析模式："歌手 - 歌名" 或 "歌名 - 歌手"</summary>
    private static (string Title, string Artist) ParseFileName(string fileName)
    {
        var parts = Regex.Split(fileName, @"[-_—]|\s{2,}");
        if (parts.Length >= 2)
        {
            // 尝试识别：如果第二部分包含常见歌手名模式（如中文名），可能是"歌手 - 歌名"
            var candidateArtist = parts[0].Trim();
            var candidateTitle = parts[1].Trim();
            
            // 简单启发式：如果第一部分像中文名（连续中文字符较多）→ 歌手在前
            if (Regex.IsMatch(candidateArtist, @"[\u4e00-\u9fa5]{2,}") && 
                !candidateTitle.Contains("专辑", StringComparison.OrdinalIgnoreCase))
            {
                return (candidateTitle, candidateArtist);
            }
        }
        
        // 兜底：全作为标题
        return (fileName, "本地音源");
    }

    /// <summary>按关键词检索。</summary>
    public List<Track> Search(string kw)
    {
        kw = (kw ?? "").Trim();
        if (string.IsNullOrEmpty(kw)) return new List<Track>();
        lock (_lock)
            return _tracks.Where(t =>
                t.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>添加播放历史</summary>
    public void AddPlayHistory(string title, string artist)
    {
        lock (_lock)
        {
            var history = new PlayHistory(title, artist, DateTime.UtcNow);
            _history.Insert(0, history);
            SaveHistory();
        }
    }

    /// <summary>获取播放历史（倒序）</summary>
    public IReadOnlyList<PlayHistory> GetPlayHistory(int count = 20)
    {
        lock (_lock) return _history.Take(count).ToList();
    }

    public int Count { get { lock (_lock) return _tracks.Count; } }
    
    /// <summary>批量导入结果</summary>
    public sealed class ImportBatchResult
    {
        public int TotalFiles { get; set; }
        public int CurrentIndex { get; set; }
        public int FailedFiles { get; set; }
        public List<Track> SuccessfulImportsList { get; set; } = new();
    }
}