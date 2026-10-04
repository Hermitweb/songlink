namespace SongLink.Host.Adapters.UiaPick.Ocr;

/// <summary>设备无关矩形（避免依赖 WinRT/WinForms 类型）。</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int px, int py) => px >= X && py >= Y && px < Right && py < Bottom;
}

/// <summary>OCR 识别出的一个词及其屏幕包围盒。</summary>
public sealed record OcrWord(string Text, Rect Box)
{
    public (int X, int Y) Center => (Box.X + Box.Width / 2, Box.Y + Box.Height / 2);
}

/// <summary>一次 OCR 识别结果。</summary>
public sealed class OcrResult
{
    public List<OcrWord> Words { get; } = new();
}

/// <summary>
/// OCR 引擎抽象（纯托管接口）。实现可换：真机用 Windows.Media.Ocr（WinRT 工程），
/// 测试用假引擎。输入是设备无关的 BGRA 像素，输出词 + 包围盒。
/// </summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }
    OcrResult Recognize(byte[] bgraPixels, int width, int height);
}
