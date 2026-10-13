using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;
using SongLink.Host.Adapters.UiaPick.Ocr;

namespace SongLink.Host.Adapters.Ocr;

/// <summary>
/// 系统 OCR 引擎（Windows.Media.Ocr，WinRT）：把 BGRA 截图识别成词 + 包围盒，供 UIA 点歌的 OCR 兜底。
/// 离线、免费、支持中英（按 languageTag）。实现 IOcrEngine（定义在 UiaPick 纯托管侧）。
/// ⚠ 需开发机带 Windows SDK 投影 pack 才能编译；本机精简 SDK 会报找不到 Windows.Media.Ocr（非代码缺陷）。
/// </summary>
public sealed class MediaOcrEngine : IOcrEngine
{
    private readonly WinOcr.OcrEngine? _engine;

    public MediaOcrEngine(string? languageTag = "zh-Hans")
    {
        try
        {
            _engine = (languageTag is not null ? WinOcr.OcrEngine.TryCreateFromLanguage(new Language(languageTag)) : null)
                      ?? WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch { _engine = null; }
    }

    public bool IsAvailable => _engine is not null;

    public OcrResult Recognize(byte[] bgraPixels, int width, int height)
    {
        var result = new OcrResult();
        if (_engine is null || width <= 0 || height <= 0) return result;

        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            bgraPixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);

        var ocr = _engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
        foreach (var line in ocr.Lines)
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                result.Words.Add(new OcrWord(w.Text, new Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height)));
            }
        return result;
    }
}
