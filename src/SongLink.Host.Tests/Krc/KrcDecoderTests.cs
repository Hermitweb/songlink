using System;
using System.Linq;
using System.Text;
using SongLink.Host.Lyrics;
using Xunit;

namespace SongLink.Host.Tests.Krc;

/// <summary>
/// KRC 解码器单元测试 —— QQ 音乐专属格式（Base64 + 16 字节密钥 XOR）。
///
/// 测试数据是**真实构造**出来的：拿已知 LRC 明文，用 KRC 密钥 XOR 后 Base64 编码，
/// 从而形成合法 KRC 输入。此前这些用例只有 Assert.True(true) / Assert.NotNull 的空壳，
/// 既没验证解密，也没验证歌词解析。
/// </summary>
public class KrcDecoderTests
{
    /// <summary>KRC 公开密钥，与 KrcDecoder 内部保持一致。</summary>
    private static readonly byte[] KrcKey =
    {
        0x1F, 0x34, 0x7A, 0xF8, 0xC3, 0xE9, 0xB2, 0xD1,
        0xA6, 0xCF, 0x57, 0x2E, 0xAB, 0xDC, 0x93, 0x4B
    };

    /// <summary>把 LRC 明文编码成合法 KRC（UTF-8 → XOR → Base64）。</summary>
    private static string MakeKrc(string lrc)
    {
        var plain = Encoding.UTF8.GetBytes(lrc);
        var encrypted = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            encrypted[i] = (byte)(plain[i] ^ KrcKey[i % KrcKey.Length]);
        }
        return Convert.ToBase64String(encrypted);
    }

    private const string SampleLrc =
        "[00:00.00]故事的小黄花\n" +
        "[00:03.50]从出生那年就飘着\n" +
        "[01:05.25]童年的荡秋千\n";

    [Fact]
    public void Decrypt_EmptyOrNullKrc_ThrowsArgumentException()
    {
        var decoder = new KrcDecoder();

        Assert.ThrowsAny<ArgumentException>(() => decoder.Decrypt(""));
        Assert.ThrowsAny<ArgumentException>(() => decoder.Decrypt(null!));
        Assert.ThrowsAny<ArgumentException>(() => decoder.Decrypt("   "));
    }

    [Fact]
    public void Decrypt_NonBase64Input_ThrowsFormatException()
    {
        var decoder = new KrcDecoder();

        Assert.Throws<FormatException>(() => decoder.Decrypt("这不是 base64 !!!"));
    }

    [Fact]
    public void Decrypt_ValidKrc_ReturnsOriginalLrcText()
    {
        var decoder = new KrcDecoder();

        var lrc = decoder.Decrypt(MakeKrc(SampleLrc));

        Assert.Equal(SampleLrc, lrc);
    }

    [Fact]
    public void Decrypt_HandlesMultibyteTextCorrectly()
    {
        var decoder = new KrcDecoder();
        // 构造多字节内容，确认 XOR 按字节而非按字符进行
        var withEmoji = "[00:00.00]测试 🎵 混合内容\n[00:02.00]第二行";

        var result = decoder.Decrypt(MakeKrc(withEmoji));

        Assert.Equal(withEmoji, result);
        Assert.Contains("🎵", result);
    }

    [Fact]
    public void ParseToLyricFrame_ValidKrc_ParsesTimelineLines()
    {
        var decoder = new KrcDecoder();

        var lyric = decoder.ParseToLyricFrame(MakeKrc(SampleLrc), "track:test");

        Assert.Equal("track:test", lyric.TrackId);
        Assert.Equal(3, lyric.Lines.Count);

        Assert.Equal(0.00, lyric.Lines[0].T, 2);
        Assert.Equal("故事的小黄花", lyric.Lines[0].X);

        Assert.Equal(3.50, lyric.Lines[1].T, 2);
        Assert.Equal("从出生那年就飘着", lyric.Lines[1].X);

        // 1 分 05.25 秒 = 65.25
        Assert.Equal(65.25, lyric.Lines[2].T, 2);
        Assert.Equal("童年的荡秋千", lyric.Lines[2].X);
    }

    [Fact]
    public void ParseToLyricFrame_TimeTagWithoutFraction_ParsesAsWholeSecond()
    {
        var decoder = new KrcDecoder();

        var lyric = decoder.ParseToLyricFrame(MakeKrc("[00:07]整秒标签"), "t1");

        var line = Assert.Single(lyric.Lines);
        Assert.Equal(7.00, line.T, 2);
        Assert.Equal("整秒标签", line.X);
    }

    [Fact]
    public void ParseToLyricFrame_GarbageInput_ReturnsEmptyLinesInsteadOfThrowing()
    {
        var decoder = new KrcDecoder();

        // 合法 Base64 但解出来不是歌词 —— 解码器应宽容返回空时间轴，而不是抛异常。
        // （原用例在这里断言 ThrowsAny，但产品行为是"不抛"，两边语义本就对不上。）
        var lyric = decoder.ParseToLyricFrame(MakeKrc("this is not a lyric at all"), "track:garbage");

        Assert.Equal("track:garbage", lyric.TrackId);
        Assert.Empty(lyric.Lines);
    }

    [Fact]
    public void Dispose_SetsIsDisposed_AndIsIdempotent()
    {
        var decoder = new KrcDecoder();
        Assert.False(decoder.IsDisposed);

        decoder.Dispose();
        Assert.True(decoder.IsDisposed);

        // 重复释放不应抛异常
        Assert.Null(Record.Exception(() => decoder.Dispose()));
    }
}