namespace SongLink.Host.Adapters.UiaPick.Ocr;

/// <summary>
/// OCR 点歌匹配策略（纯逻辑、可本机测）：从 OCR 词集中，在可选结果区域内，
/// 找与查询（歌名/艺人）最匹配的词，返回其中心点供点击。这是"截图→OCR→点击"兜底路径里
/// 唯一与设备无关、可单测的核心。
/// </summary>
public static class OcrPickStrategy
{
    /// <summary>返回最佳匹配词的中心点；无匹配返回 null。</summary>
    public static (int X, int Y)? FindClickPoint(OcrResult ocr, string query, Rect? region = null)
    {
        var best = FindBestWord(ocr, query, region);
        return best?.Center;
    }

    public static OcrWord? FindBestWord(OcrResult ocr, string query, Rect? region = null)
    {
        if (ocr is null || string.IsNullOrWhiteSpace(query)) return null;
        var q = Normalize(query);
        OcrWord? best = null;
        double bestScore = 0;

        foreach (var w in ocr.Words)
        {
            if (region is not null && !region.Value.Contains(w.Center.X, w.Center.Y)) continue;
            var s = Score(q, Normalize(w.Text));
            if (s > bestScore) { bestScore = s; best = w; }
        }
        return bestScore >= 0.34 ? best : null;   // 低于阈值视为无匹配
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());

    private static double Score(string q, string t)
    {
        if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(t)) return 0;
        if (t == q) return 1.0;
        if (t.Contains(q)) return 0.85 + 0.1 * ((double)q.Length / t.Length);   // OCR 词含查询
        if (q.Contains(t) && t.Length >= 2) return 0.6 * ((double)t.Length / q.Length); // 查询含 OCR 词
        // 字符重叠（Jaccard）兜底
        var qs = q.ToHashSet(); var ts = t.ToHashSet();
        int inter = qs.Count(ts.Contains);
        int union = qs.Count + ts.Count - inter;
        return union == 0 ? 0 : 0.5 * (double)inter / union;
    }
}
