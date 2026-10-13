namespace SongLink.Host;

/// <summary>
/// 路径安全的文件名片段工具 — 去掉 OS 不允许的字符并 Trim。
///
/// 用于：
/// - LocalLibrary 缓存文件 (audio + index.json 路径)
/// - LyricServiceExtensions 缓存文件 (LRC 文件名)
/// - LyricService.Key 索引查找
///
/// 三处必须保持同一规则，否则写入文件名和读回查找会不一致。
/// </summary>
public static class PathSafeName
{
    /// <summary>把任意字符串过滤成可作为文件名的片段。</summary>
    public static string Sanitize(string s) =>
        new string((s ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
}
