namespace SongLink.Host.Adapters.UiaPick.Ocr;

/// <summary>
/// 离线/占位 OCR 引擎：IsAvailable=true 但 Recognize 返回空结果（或注入的固定结果）。
/// 用途：① 无系统 OCR 环境下的安全降级；② 组合根反射装配路径的测试靶（MediaOcrEngine 是 WinRT，本机不可编）。
/// </summary>
public sealed class StaticOcrEngine : IOcrEngine
{
    private readonly OcrResult? _fixed;
    public string LanguageTag { get; }

    public StaticOcrEngine(string? languageTag = "zh-Hans") : this(languageTag, null) { }

    public StaticOcrEngine(string? languageTag, OcrResult? fixedResult)
    {
        LanguageTag = languageTag ?? "zh-Hans";
        _fixed = fixedResult;
    }

    public bool IsAvailable => true;

    public OcrResult Recognize(byte[] bgraPixels, int width, int height)
        => _fixed ?? new OcrResult();
}
