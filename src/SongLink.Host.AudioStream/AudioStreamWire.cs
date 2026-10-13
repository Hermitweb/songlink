using System;

namespace SongLink.Host.AudioStream;

/// <summary>
/// 协议 §4 PHONE_STREAM 帧格式 (大端 / 网络字节序)：
/// <code>
/// +--------+--------+---------+---------+------------------+
/// | ver(1) | flags  | seq(4)  | pts(8)  | payloadLen(2)    | opusFrame...
/// +--------+--------+---------+---------+------------------+
/// </code>
/// - ver 必须 = <see cref="WireVersion"/> = 1
/// - flags：bit0 = 首片/关键帧，bit1 = 末片
/// - seq：丢帧检测用的单调递增序号
/// - pts：90kHz 或采样帧号（本地约定：每帧采样数）
/// - payloadLen：payloadLen 字段后的 opus 字节数（≤ ushort.MaxValue = 65535）
/// </summary>
public static class AudioStreamWire
{
    public const int WireVersion = 1;

    public const byte FlagFirst = 0b0000_0001;   // bit0
    public const byte FlagLast  = 0b0000_0010;   // bit1

    private const int HeaderSize = 1 + 1 + 4 + 8 + 2;   // 16 字节

    public readonly record struct Frame(
        byte Version,
        byte Flags,
        uint Seq,
        ulong Pts,
        ushort PayloadLen,
        ReadOnlyMemory<byte> Payload);

    /// <summary>计算一帧（不含 payloadLen）的总字节数。</summary>
    public static int FrameTotalSize(ushort payloadLen) => HeaderSize + payloadLen;

    /// <summary>组装 §4 帧。</summary>
    public static byte[] Assemble(
        byte flags, uint seq, ulong pts, ReadOnlySpan<byte> opusFrame)
    {
        if (opusFrame.Length > ushort.MaxValue)
            throw new ArgumentException($"opus 帧过大：{opusFrame.Length} > 65535", nameof(opusFrame));

        var packet = new byte[HeaderSize + opusFrame.Length];
        packet[0] = WireVersion;
        packet[1] = flags;
        WriteUInt32BE(packet.AsSpan(2, 4), seq);
        WriteUInt64BE(packet.AsSpan(6, 8), pts);
        WriteUInt16BE(packet.AsSpan(14, 2), (ushort)opusFrame.Length);
        opusFrame.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>
    /// 解析 §4 帧。校验 ver、payloadLen 边界，不复制 payload（用 ReadOnlyMemory 引用原 buffer）。
    /// </summary>
    public static Frame Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize)
            throw new InvalidOperationException($"§4 帧头不完整: 至少需 {HeaderSize} 字节, 实际 {packet.Length}");

        var ver = packet[0];
        if (ver != WireVersion)
            throw new InvalidOperationException($"§4 帧版本不受支持: ver={ver}, 期望 {WireVersion}");

        var flags = packet[1];
        var seq = ReadUInt32BE(packet.Slice(2, 4));
        var pts = ReadUInt64BE(packet.Slice(6, 8));
        var payloadLen = ReadUInt16BE(packet.Slice(14, 2));

        if (packet.Length < HeaderSize + payloadLen)
            throw new InvalidOperationException(
                $"§4 帧 payload 不完整: 声明 {payloadLen} 字节, 实际余 {packet.Length - HeaderSize}");

        // ReadOnlyMemory 不持有所有权——调用方负责原 buffer 的生命周期
        var payload = packet.Slice(HeaderSize, payloadLen).ToArray();
        return new Frame(ver, flags, seq, pts, payloadLen, payload);
    }

    private static void WriteUInt16BE(Span<byte> dst, ushort v)
    {
        dst[0] = (byte)(v >> 8);
        dst[1] = (byte)v;
    }

    private static void WriteUInt32BE(Span<byte> dst, uint v)
    {
        dst[0] = (byte)(v >> 24);
        dst[1] = (byte)(v >> 16);
        dst[2] = (byte)(v >> 8);
        dst[3] = (byte)v;
    }

    private static void WriteUInt64BE(Span<byte> dst, ulong v)
    {
        dst[0] = (byte)(v >> 56);
        dst[1] = (byte)(v >> 48);
        dst[2] = (byte)(v >> 40);
        dst[3] = (byte)(v >> 32);
        dst[4] = (byte)(v >> 24);
        dst[5] = (byte)(v >> 16);
        dst[6] = (byte)(v >> 8);
        dst[7] = (byte)v;
    }

    private static ushort ReadUInt16BE(ReadOnlySpan<byte> src)
        => (ushort)((src[0] << 8) | src[1]);

    private static uint ReadUInt32BE(ReadOnlySpan<byte> src)
        => (uint)((src[0] << 24) | (src[1] << 16) | (src[2] << 8) | src[3]);

    private static ulong ReadUInt64BE(ReadOnlySpan<byte> src)
        => ((ulong)src[0] << 56) | ((ulong)src[1] << 48) | ((ulong)src[2] << 40) | ((ulong)src[3] << 32)
           | ((ulong)src[4] << 24) | ((ulong)src[5] << 16) | ((ulong)src[6] << 8) | src[7];
}
