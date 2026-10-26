using System.IO.Compression;
using System.Text;
using SongLink.Protocol;

namespace SongLink.Host.Lyrics;

/// <summary>
/// QQ 音乐 KRC 格式解码器。
///
/// <para>标准 QQ KRC 结构（参照 mzlogin/openapi、PyQqlib 等开源实现）：</para>
/// <code>
///   KRC base64 解码
///   → 16 字节密钥**滚动 XOR**（key 指针在 16 字节内循环前进，等价逐字节 key[i % 16]）
///   → 载荷布局：[2 字节 LRC 长度(大端)] [LRC 明文 或 zlib(LRC 明文)] [8 字节 KRC 尾标]
///   → 若正文以 0x78 0x9C 开头（zlib RFC1950 magic）则 zlib 解压；否则按明文 LRC 直通
///   → 裁掉明文尾部 '\0' 填充
/// </code>
///
/// <para>
/// 旧实现只做了 XOR 就直接当明文，既不裁 2 字节长度前缀也不裁 8 字节尾标、更没有 zlib 解压，
/// 真实 KRC 样本解出来是乱码。本版按上述标准结构重写；为兼容不同客户端版本的帧布局差异，
/// 抽取正文时用 <see cref="LooksLikeLrc"/> 做自适应校验（多策略、择优），而不是硬编码单一偏移。
/// </para>
/// </summary>
public sealed class KrcDecoder : IDisposable
{
    /// <summary>
    /// QQ KRC 公开密钥。各开源实现给出的 16 字节值一致：
    /// <c>1F 34 7A F8 C3 E9 B2 D1 A6 CF 57 2E AB DC 93 4B</c>。
    /// </summary>
    internal static readonly byte[] KrcKey =
    {
        0x1F, 0x34, 0x7A, 0xF8, 0xC3, 0xE9, 0xB2, 0xD1,
        0xA6, 0xCF, 0x57, 0x2E, 0xAB, 0xDC, 0x93, 0x4B
    };

    private const int LengthPrefixBytes = 2;
    private const int TailMarkerBytes = 8;

    private bool _disposed;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 解码 KRC base64 字符串为 LRC 文本。
    /// 步骤：Base64 → 滚动 XOR → 自适应抽取正文（去 2 字节长度前缀 / 8 字节尾标，zlib 或明文）→ UTF-8。
    /// </summary>
    public string Decrypt(string? krcContent)
    {
        if (string.IsNullOrWhiteSpace(krcContent))
            throw new ArgumentException("KRC content cannot be empty");

        var encryptedData = Convert.FromBase64String(krcContent.Trim());
        if (encryptedData.Length < LengthPrefixBytes + TailMarkerBytes + 1)
            throw new ArgumentException(
                $"KRC 数据过短（{encryptedData.Length} 字节），不足以构成合法的 KRC 载荷");

        // 滚动 XOR：key 指针逐字节在 16 字节密钥内循环（等价 key[i % 16]）
        var xor = new byte[encryptedData.Length];
        for (int i = 0; i < encryptedData.Length; i++)
            xor[i] = (byte)(encryptedData[i] ^ KrcKey[i % KrcKey.Length]);

        return ExtractLrc(xor);
    }

    /// <summary>从 KRC 字符串直接解析为 Lyric 帧。</summary>
    public Lyric ParseToLyricFrame(string krcContent, string trackId)
    {
        var lrcText = Decrypt(krcContent);
        var lines = LyricService.Parse(lrcText);

        return new Lyric
        {
            TrackId = trackId,
            OffsetMs = 0,
            Lines = lines
        };
    }

    // ------------------------------------------------------------------ 正文抽取（自适应）

    /// <summary>
    /// 从 XOR 后的原始字节中挑出 LRC 正文。不同客户端版本在长度前缀 / 尾标 / 是否压缩上
    /// 有差异，按优先级尝试：
    ///   1. 标准帧布局且 2 字节长度前缀自洽（declared == 正文长度）→ 裁 2+8 取正文；
    ///   2. 无帧布局（整段就是正文，bare XOR 历史形态）→ 整段直通；
    ///   3. 标准帧布局但长度前缀陈旧/为零 → 仍裁 2+8（此时整段不像 LRC，前缀不可信）；
    ///   4. 兜底：整段。
    /// 每步用 <see cref="LooksLikeLrc"/> 校验，首个通过即采用。
    /// </summary>
    private static string ExtractLrc(byte[] data)
    {
        if (data.Length < LengthPrefixBytes + TailMarkerBytes)
        {
            // 太短，不可能是标准帧
            var shortBody = DecodeBody(data);
            if (shortBody is not null) return shortBody;
            return Encoding.UTF8.GetString(data).TrimEnd('\0');
        }

        int bodyLen = data.Length - LengthPrefixBytes - TailMarkerBytes;
        int declared = (data[0] << 8) | data[1];

        // 1. 长度前缀自洽 → 标准帧布局，可信地裁
        if (declared == bodyLen)
        {
            var s = DecodeBody(data.AsSpan(LengthPrefixBytes, bodyLen));
            if (s is not null) return s;
        }

        // 2. 无帧布局（bare）：整段像 LRC 就用整段
        //    （bare 样本首 2 字节是 LRC 文本本身，比如 "[0"=0x5B30，与正文长度对不上，
        //      所以走不到步骤 1）
        var whole = DecodeBody(data);
        if (whole is not null) return whole;

        // 3. 长度前缀陈旧/为零（某些版本写 0 或写残）：整段不像 LRC（首 2 字节是控制字符），
        //    只能信标准帧布局
        var framed = DecodeBody(data.AsSpan(LengthPrefixBytes, bodyLen));
        if (framed is not null) return framed;

        // 4. 兜底：整段按 UTF-8，裁尾部 null（保留解码器“不抛、宽容返回”的既有契约）
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    /// <summary>把“正文”字节解码为 LRC 文本；不像 LRC 返回 null（交给上层换策略）。</summary>
    private static string? DecodeBody(ReadOnlySpan<byte> body)
    {
        string? text = null;
        if (body.Length >= 2 && body[0] == 0x78 && body[1] == 0x9C)
        {
            // zlib（RFC1950）压缩体
            try { text = InflateZlib(body); }
            catch { text = null; }
        }
        else
        {
            text = Encoding.UTF8.GetString(body).TrimEnd('\0');
        }

        return LooksLikeLrc(text) ? text : null;
    }

    /// <summary>zlib inflate（.NET 的 ZLibStream 可解 RFC1950 流；KRC 压缩体与此兼容）。</summary>
    private static string InflateZlib(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\0');
    }

    /// <summary>
    /// 判断一段文本是否“像 LRC”：前 128 字符内出现 LRC 时间戳 <c>[mm:ss</c> 或
    /// 元数据标签 <c>[ti:/[ar:/[al:</c>。用于在多种帧布局里挑出正确的那个。
    /// </summary>
    internal static bool LooksLikeLrc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var probe = s.Length > 128 ? s[..128] : s;
        return probe.Contains("[0", StringComparison.Ordinal)   // [00:..] 起头的时间戳
            || probe.Contains("[ti:", StringComparison.OrdinalIgnoreCase)
            || probe.Contains("[ar:", StringComparison.OrdinalIgnoreCase)
            || probe.Contains("[al:", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(probe, @"\[\d{1,2}:\d{2}([.:]\d{1,2})?\]");
    }

    public void Dispose() => _disposed = true;
}
