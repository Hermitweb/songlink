using System.Buffers.Binary;

namespace SongLink.Spikes.AudioSink;

/// <summary>
/// 音频分片头解析，布局与《协议契约.md》§4 严格一致（大端）：
///   ver(1) | flags(1) | seq(4) | pts(8) | payloadLen(2) | opusPayload...
/// 头共 16 字节。
/// </summary>
public readonly struct AudioFrame
{
    public const int HeaderLen = 16;
    public const byte FlagFirst = 0x01; // bit0 首片/关键帧
    public const byte FlagLast  = 0x02; // bit1 末片

    public byte Version { get; init; }
    public byte Flags { get; init; }
    public uint Seq { get; init; }
    public ulong Pts { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }

    public bool IsFirst => (Flags & FlagFirst) != 0;
    public bool IsLast  => (Flags & FlagLast) != 0;

    /// <summary>解析一个 UDP 数据报；非法返回 null。</summary>
    public static AudioFrame? Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderLen) return null;
        byte ver = datagram[0];
        if (ver != 1) return null;
        byte flags = datagram[1];
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(2, 4));
        ulong pts = BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(6, 8));
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(14, 2));
        int available = datagram.Length - HeaderLen;
        int take = Math.Min(len, available);
        var payload = datagram.Slice(HeaderLen, take).ToArray();
        return new AudioFrame
        {
            Version = ver, Flags = flags, Seq = seq, Pts = pts,
            Payload = payload
        };
    }
}
