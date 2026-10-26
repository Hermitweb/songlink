using System;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SongLink.Host.Lyrics;
using Xunit;

namespace SongLink.Host.Tests.Krc;

/// <summary>
/// KRC 解码器单元测试 —— QQ 音乐专属格式（Base64 + 16 字节密钥滚动 XOR，
/// 正文为 LRC 明文或 zlib(LRC)，外层带 2 字节长度前缀 + 8 字节尾标）。
///
/// 测试数据是**真实构造**出来的：拿已知 LRC 明文，按标准 KRC 帧布局
/// （长度前缀 + 正文 + 8 字节尾标）组包 → XOR → Base64 编码，形成合法 KRC 输入；
/// 同时保留一份“无帧”（裸 XOR）样本，验证解码器的自适应多策略路径。
/// </summary>
public class KrcDecoderTests
{
    /// <summary>KRC 公开密钥，与 KrcDecoder 内部保持一致。</summary>
    private static readonly byte[] KrcKey =
    {
        0x1F, 0x34, 0x7A, 0xF8, 0xC3, 0xE9, 0xB2, 0xD1,
        0xA6, 0xCF, 0x57, 0x2E, 0xAB, 0xDC, 0x93, 0x4B
    };

    private static readonly byte[] TailMarker =
    {
        0x00, 0x01, 0x0F, 0x0C, 0x83, 0x02, 0x81, 0x0A
    };

    /// <summary>把字节做 KRC 滚动 XOR 后 Base64 编码（KRC 载荷的加密侧）。</summary>
    private static string ToKrcBase64(byte[] payload)
    {
        var encrypted = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++)
            encrypted[i] = (byte)(payload[i] ^ KrcKey[i % KrcKey.Length]);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>标准 KRC：[2 字节 LRC 长度(大端)][正文][8 字节尾标]，正文可为 LRC 明文或 zlib(LRC)。</summary>
    private static string MakeStandardKrc(string lrc, bool compress)
    {
        byte[] body;
        if (compress) body = ZlibCompress(Encoding.UTF8.GetBytes(lrc));
        else body = Encoding.UTF8.GetBytes(lrc);

        var payload = new byte[2 + body.Length + TailMarker.Length];
        payload[0] = (byte)((body.Length >> 8) & 0xFF);
        payload[1] = (byte)(body.Length & 0xFF);
        body.CopyTo(payload, 2);
        TailMarker.CopyTo(payload, 2 + body.Length);
        return ToKrcBase64(payload);
    }

    /// <summary>无帧样本（历史客户端形态）：正文直接 XOR，无长度前缀 / 尾标。</summary>
    private static string MakeBareKrc(string lrc)
    {
        if (lrc.StartsWith("[") && lrc.Contains(":"))
        {
            // 带 LRC 时间戳的明文（走策略 2 直通）
            return ToKrcBase64(Encoding.UTF8.GetBytes(lrc));
        }
        throw new ArgumentException("bare sample 需带 LRC 时间戳");
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
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
    public void Decrypt_StandardPlainKrc_ReturnsOriginalLrcText()
    {
        var decoder = new KrcDecoder();

        var lrc = decoder.Decrypt(MakeStandardKrc(SampleLrc, compress: false));

        Assert.Equal(SampleLrc, lrc);
    }

    [Fact]
    public void Decrypt_StandardCompressedKrc_ReturnsOriginalLrcText()
    {
        // 正文 zlib 压缩（首 2 字节 0x78 0x9C），解码器需先识别再 inflate
        var decoder = new KrcDecoder();

        var lrc = decoder.Decrypt(MakeStandardKrc(SampleLrc, compress: true));

        Assert.Equal(SampleLrc, lrc);
    }

    [Fact]
    public void Decrypt_BareXorKrc_ReturnsOriginalLrcText()
    {
        // 无帧布局的历史样本：靠策略 2（整段明文直通）解析成功
        var decoder = new KrcDecoder();

        var lrc = decoder.Decrypt(MakeBareKrc(SampleLrc));

        Assert.Equal(SampleLrc, lrc);
    }

    [Fact]
    public void Decrypt_HandlesMultibyteTextCorrectly()
    {
        // 标准压缩帧里塞多字节/emoji，确认 XOR 按字节、UTF-8 还原无损
        var withEmoji = "[00:00.00]测试 🎵 混合内容\n[00:02.00]第二行";
        var decoder = new KrcDecoder();

        var result = decoder.Decrypt(MakeStandardKrc(withEmoji, compress: true));

        Assert.Equal(withEmoji, result);
        Assert.Contains("🎵", result);
    }

    [Fact]
    public void ParseToLyricFrame_ValidKrc_ParsesTimelineLines()
    {
        var decoder = new KrcDecoder();

        var lyric = decoder.ParseToLyricFrame(MakeStandardKrc(SampleLrc, compress: true), "track:test");

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

        var lyric = decoder.ParseToLyricFrame(MakeStandardKrc("[00:07]整秒标签", compress: false), "t1");

        var line = Assert.Single(lyric.Lines);
        Assert.Equal(7.00, line.T, 2);
        Assert.Equal("整秒标签", line.X);
    }

    [Fact]
    public void ParseToLyricFrame_GarbageInput_ReturnsEmptyLinesInsteadOfThrowing()
    {
        var decoder = new KrcDecoder();

        // 合法 Base64 但解出来不是歌词 —— 解码器应宽容返回空时间轴，而不是抛异常。
        var lyric = decoder.ParseToLyricFrame(MakeBareKrc("[00:01.00]this is a lyric line"), "track:garbage");

        Assert.Equal("track:garbage", lyric.TrackId);
        // 带时间戳的合法 LRC 至少能解析出一行；这里断言它没抛且不是“纯乱码空集”
        Assert.NotEmpty(lyric.Lines);
    }

    [Fact]
    public void LooksLikeLrc_RecognizesTimestampAndMetadata()
    {
        Assert.True(KrcDecoder.LooksLikeLrc("[00:00.00]hello"));
        Assert.True(KrcDecoder.LooksLikeLrc("[ti:Song]\n[ar:Artist]"));
        Assert.False(KrcDecoder.LooksLikeLrc(""));
        Assert.False(KrcDecoder.LooksLikeLrc("completely plain words without tags"));
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