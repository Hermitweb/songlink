using SongLink.Host;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// PathSafeName.Sanitize 跨三处（LocalLibrary / LyricServiceExtensions / LyricService.Key）
/// 的契约测试 — 必须统一规则,否则写入文件名 ≠ 读回查找路径,缓存永远 miss。
/// </summary>
public class PathSafeNameTests
{
    [Theory]
    [InlineData("晴天 - 周杰伦", "晴天 - 周杰伦")]   // 中文/空格不非法
    [InlineData("A/B?C", "ABC")]                    // / ? 是非法字符
    [InlineData("  spaced  ", "spaced")]            // Trim
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Sanitize_removes_invalid_chars_and_trims(string? input, string expected)
    {
        Assert.Equal(expected, PathSafeName.Sanitize(input!));
    }

    [Fact]
    public void Sanitize_keeps_unicode_punctuation_alone()
    {
        // 中文标点不在 Windows GetInvalidFileNameChars 里(只有 \ / : * ? " < > |)
        // 所以应保留
        Assert.Equal("晴天——周杰伦(feat.路人甲)",
            PathSafeName.Sanitize("晴天——周杰伦(feat.路人甲)"));
    }
}
