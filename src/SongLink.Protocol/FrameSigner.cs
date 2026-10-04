using System.Security.Cryptography;
using System.Text;

namespace SongLink.Protocol;

/// <summary>
/// 每帧 HMAC-SHA256 签名（契约 §6）。key = 配对下发的 token；
/// canonical = 该帧去掉 sig 后的 JSON（FrameCodec 用 WhenWritingNull 省略 null sig）。
/// 纯托管、可本机测。用于入站命令验签，防同网设备伪造指令。
/// </summary>
public static class FrameSigner
{
    /// <summary>计算帧的签名（十六进制大写）。会临时清空再恢复 frame.Sig。</summary>
    public static string Compute(string key, Frame frame)
    {
        var saved = frame.Sig;
        frame.Sig = null;
        try
        {
            var canonical = FrameCodec.Serialize(frame);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }
        finally { frame.Sig = saved; }
    }

    /// <summary>就地给 frame 盖上签名。</summary>
    public static void Sign(string key, Frame frame) => frame.Sig = Compute(key, frame);

    /// <summary>校验 frame 自带 sig 是否有效（sig 缺失/不匹配返回 false）。</summary>
    public static bool Verify(string key, Frame frame)
    {
        if (string.IsNullOrEmpty(frame.Sig)) return false;
        var expected = Compute(key, frame);
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(frame.Sig);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
