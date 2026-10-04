namespace SongLink.Host;

/// <summary>
/// 封面图存储（架构 §3.7 / 评审 #4）：按 ref 从 art 目录取图，供 GET /art/{ref} 下发。
/// 含路径穿越防护——ref 只允许 [A-Za-z0-9._-] 且不含 ".."。纯逻辑、可单测。
/// </summary>
public sealed class ArtStore
{
    private readonly string _dir;
    private static readonly (string Ext, string Mime)[] Exts =
    {
        (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg"), (".webp", "image/webp")
    };

    public ArtStore(string? dir = null) => _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "art");

    /// <summary>ref 是否安全（防目录穿越）。空、含非法字符或 ".." 均判非法。</summary>
    public static bool IsSafeRef(string? refId) =>
        !string.IsNullOrEmpty(refId)
        && refId.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
        && !refId.Contains("..");

    /// <summary>按 ref 取图字节 + MIME；非法或不存返回 null。</summary>
    public (byte[] Bytes, string Mime)? Get(string? refId)
    {
        if (!IsSafeRef(refId)) return null;
        foreach (var (ext, mime) in Exts)
        {
            var path = Path.Combine(_dir, refId + ext);
            if (File.Exists(path)) return (File.ReadAllBytes(path), mime);
        }
        return null;
    }
}
