using System.Buffers.Binary;

namespace SongLink.Protocol;

/// <summary>
/// 音频分片（《协议契约.md》§4，大端）：
///   ver(1) | flags(1) | seq(4) | pts(8) | payloadLen(2) | opusPayload...
/// 头共 16 字节。纯托管、跨栈共享（Host 接收 / Remote 打包）。
/// </summary>
public readonly struct AudioFrame
{
    public const int HeaderLen = 16;
    public const byte Version = 1;
    public const byte FlagFirst = 0x01; // bit0 首片/关键帧
    public const byte FlagLast  = 0x02; // bit1 末片

    public byte Flags { get; init; }
    public uint Seq { get; init; }
    public ulong Pts { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }

    public bool IsFirst => (Flags & FlagFirst) != 0;
    public bool IsLast  => (Flags & FlagLast) != 0;

    /// <summary>解析一个 UDP 数据报；非法（太短/版本不符/长度越界）返回 null。</summary>
    public static AudioFrame? Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderLen || datagram[0] != Version) return null;
        byte flags = datagram[1];
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(2, 4));
        ulong pts = BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(6, 8));
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(14, 2));
        int available = datagram.Length - HeaderLen;
        if (len > available) return null;                       // 声明长度越界 → 视为坏包丢弃
        return new AudioFrame
        {
            Flags = flags, Seq = seq, Pts = pts,
            Payload = datagram.Slice(HeaderLen, len).ToArray()
        };
    }

    /// <summary>打包为一个可发送的 UDP 数据报（供 Remote/测试使用）。</summary>
    public static byte[] Pack(byte flags, uint seq, ulong pts, ReadOnlySpan<byte> opusPayload)
    {
        var dgram = new byte[HeaderLen + opusPayload.Length];
        dgram[0] = Version; dgram[1] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(dgram.AsSpan(2, 4), seq);
        BinaryPrimitives.WriteUInt64BigEndian(dgram.AsSpan(6, 8), pts);
        BinaryPrimitives.WriteUInt16BigEndian(dgram.AsSpan(14, 2), (ushort)opusPayload.Length);
        opusPayload.CopyTo(dgram.AsSpan(HeaderLen));
        return dgram;
    }
}
