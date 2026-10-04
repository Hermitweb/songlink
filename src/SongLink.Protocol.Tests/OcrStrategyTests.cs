using SongLink.Host.Adapters.UiaPick.Ocr;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>OCR 点歌匹配策略（纯逻辑）测试——"截图→OCR→点击"兜底路径里唯一与设备无关、可本机测的核心。</summary>
public class OcrStrategyTests
{
    private static OcrResult Result(params (string text, int x, int y, int w, int h)[] words)
    {
        var r = new OcrResult();
        foreach (var (t, x, y, w, h) in words) r.Words.Add(new OcrWord(t, new Rect(x, y, w, h)));
        return r;
    }

    [Fact]
    public void Picks_exact_match_center()
    {
        var ocr = Result(("周杰伦", 10, 10, 60, 20), ("晴天", 20, 50, 40, 20));
        var pt = OcrPickStrategy.FindClickPoint(ocr, "晴天");
        Assert.NotNull(pt);
        Assert.Equal((40, 60), pt!.Value);   // 20+40/2, 50+20/2
    }

    [Fact]
    public void Picks_substring_match()
    {
        var ocr = Result(("稻香 - 周杰伦", 100, 200, 120, 24));
        var best = OcrPickStrategy.FindBestWord(ocr, "稻香");
        Assert.NotNull(best);
        Assert.Contains("稻香", best!.Text);
    }

    [Fact]
    public void Respects_region_filter()
    {
        var ocr = Result(("晴天", 10, 10, 40, 20), ("晴天", 300, 400, 40, 20));
        // 只在结果区域 (x>=200) 内找
        var best = OcrPickStrategy.FindBestWord(ocr, "晴天", new Rect(200, 300, 400, 300));
        Assert.NotNull(best);
        Assert.Equal(300, best!.Box.X);   // 选中区域内的第二个
    }

    [Fact]
    public void Returns_null_when_no_match()
    {
        var ocr = Result(("设置", 10, 10, 40, 20), ("菜单", 80, 10, 40, 20));
        Assert.Null(OcrPickStrategy.FindClickPoint(ocr, "完全不相干的歌名xyz"));
    }

    [Fact]
    public void Ignores_punctuation_and_case()
    {
        var ocr = Result(("Yellow", 50, 50, 60, 20));
        Assert.NotNull(OcrPickStrategy.FindBestWord(ocr, " yellow! "));
    }
}
