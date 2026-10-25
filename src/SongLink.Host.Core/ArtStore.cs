namespace SongLink.Host;

/// <summary>
/// 封面图存储（架构 §3.7 / 评审 #4）：按 ref 从 art 目录取图，供 GET /art/{ref} 下发；
/// POST /art?name={ref} 走 <see cref="Save"/> 写入（手机把正在播放的封面交给主机）。
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

    /// <summary>ref 自带的扩展名是否在 <see cref="Exts"/> 白名单内（.png/.jpg/.jpeg/.webp）。
    /// 上传端点用它把"不认识的后缀"判 400——避免往 art 目录写 .exe/.html 之类的东西。</summary>
    public static bool IsKnownExt(string? refId) =>
        refId is not null
        && Exts.Any(e => refId.EndsWith(e.Ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>写入封面字节（目录不存在则创建），供 POST /art?name={ref} 调用。
    /// <see cref="IsSafeRef"/> 仍是第一道防线——非法 ref 一律拒绝，不落到磁盘上。
    /// 失败（磁盘满/无权限/非法 ref）一律吞掉返回 false，由 HTTP 层映射成 500，不把
    /// 异常细节透给局域网上的客户端。</summary>
    public bool Save(string? refId, byte[]? bytes)
    {
        if (refId is null || bytes is null) return false;
        if (!IsSafeRef(refId) || !IsKnownExt(refId)) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(Path.Combine(_dir, refId), bytes);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[art] 保存封面失败 ref={refId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>按 ref 取图字节 + MIME；非法或不存返回 null。</summary>
    public (byte[] Bytes, string Mime)? Get(string? refId)
    {
        if (!IsSafeRef(refId)) return null;
        // ref 自带扩展名时先按原名精确命中：上传写盘用的就是 "abc.png"，而安卓面板
        // (ArtImage.kt) 是把 artRef 原样拼进 /art/{ref} 的，两边必须是同一个 ref。
        // 旧的"ref 不带扩展名"用法（下面那个循环）行为完全不变。
        var hit = Exts.FirstOrDefault(e => refId!.EndsWith(e.Ext, StringComparison.OrdinalIgnoreCase));
        if (hit.Ext is not null)
        {
            var exact = Path.Combine(_dir, refId!);
            if (File.Exists(exact)) return (File.ReadAllBytes(exact), hit.Mime);
        }
        foreach (var (ext, mime) in Exts)
        {
            var path = Path.Combine(_dir, refId + ext);
            if (File.Exists(path)) return (File.ReadAllBytes(path), mime);
        }
        return null;
    }
}
