using System;
using Concentus.Enums;
using Concentus;

namespace SongLink.Host.AudioStream;

/// <summary>
/// OPUS 音频编解码器（架构 §3.8 audio:start / 契约 §4 PHONE_STREAM）：
/// 基于 Concentus 2.2.2（libopus 的 .NET 端口，纯托管实现，无原生依赖）。
/// 默认协商值 48 kHz / 立体声 / 20 ms / 单帧 960 采样；client 可在 audio:start 帧里覆盖。
/// 帧包装: <c>ver(1)|flags(1)|seq(4)|pts(8)|payloadLen(2)|opusFrame...</c> 见
/// <see cref="AudioStreamWire"/>。
/// </summary>
public sealed class OpusCodec : IDisposable
{
    /// <summary>默认帧时长（ms）。常见：10 / 20 / 40。</summary>
    public const int DefaultFrameMs = 20;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitrateBps { get; }
    public int FrameSizeSamples { get; }

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private bool _disposed;

    public OpusCodec(int sampleRate = 48000, int channels = 2, int bitrateBps = 128_000, int frameMs = DefaultFrameMs)
    {
        if (sampleRate != 8000 && sampleRate != 12000 && sampleRate != 16000 &&
            sampleRate != 24000 && sampleRate != 48000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus 仅支持 8/12/16/24/48 kHz");
        if (channels != 1 && channels != 2)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Opus 仅支持 1 或 2 通道");
        if (frameMs != 2 && frameMs != 5 && frameMs != 10 && frameMs != 20 && frameMs != 40 && frameMs != 60)
            throw new ArgumentOutOfRangeException(nameof(frameMs), frameMs, "Opus 帧时长仅支持 2.5/5/10/20/40/60 ms");

        SampleRate = sampleRate;
        Channels = channels;
        BitrateBps = bitrateBps;
        FrameSizeSamples = sampleRate * frameMs / 1000;

        // OpusCodecFactory 在支持原生 dll 时优先加载（自动启用 libopus）；
        // 失败则回落到纯托管 Concentus 实现——同一 API、同样的语义。
        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO, null);
        _encoder.Bitrate = bitrateBps;
        _encoder.UseVBR = true;
        _encoder.UseDTX = false;
        _encoder.ForceChannels = channels;

        _decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels, null);
    }

    /// <summary>期望单帧 PCM 样本总数（含全部通道）——用于校验 + 预分配 buffer。</summary>
    public int PcmSamplesPerFrame => FrameSizeSamples * Channels;

    /// <summary>
    /// PCM (16-bit 交织) → Opus 字节流。
    /// <paramref name="pcm"/> 长度必须 = <see cref="PcmSamplesPerFrame"/>。
    /// 返回新分配的 opus 包（不含 §4 帧头）。
    /// </summary>
    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodec));
        if (pcm.Length != PcmSamplesPerFrame)
            throw new ArgumentException(
                $"PCM 长度必须为 {PcmSamplesPerFrame} (= frameSize*{Channels})", nameof(pcm));

        // Concentus 推荐最大包尺寸：单帧最大 1275 + 头部开销 ~4000 容错
        Span<byte> encoded = stackalloc byte[4000];
        int n = _encoder.Encode(pcm, FrameSizeSamples, encoded, encoded.Length);
        if (n < 0) throw new InvalidOperationException($"Opus 编码失败 (code={n})");
        if (n >= encoded.Length) throw new InvalidOperationException("Opus 输出缓冲不足");

        return encoded.Slice(0, n).ToArray();
    }

    /// <summary>
    /// Opus 字节流 → PCM (16-bit 交织)。
    /// <paramref name="opusPacket"/> 必须是单帧 Opus 编码（不含 §4 帧头）；
    /// 返回长度固定为 <see cref="PcmSamplesPerFrame"/>。
    /// </summary>
    public short[] Decode(ReadOnlySpan<byte> opusPacket)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodec));
        if (opusPacket.Length == 0) throw new ArgumentException("opus 包不能为空", nameof(opusPacket));

        var pcm = new short[PcmSamplesPerFrame];
        // IOpusDecoder 接口 (Concentus 2.2.2) 只暴露 3 参数 Span 重载，启用 FEC 用 concrete.Configure。
        int n = _decoder.Decode(opusPacket, pcm, FrameSizeSamples);
        if (n < 0) throw new InvalidOperationException($"Opus 解码失败 (code={n})");
        if (n != FrameSizeSamples)
            throw new InvalidOperationException($"Opus 解码采样数异常: 期望 {FrameSizeSamples}，实际 {n}");
        return pcm;
    }

    /// <summary>
    /// 丢包 PLC（packet loss concealment）：收到 UDP 空洞时，用空包 + <c>fec=true</c>
    /// 让解码器用 Opus 内置的 PLC 续出当前音频（而不是插死静音，听感上是一声"卡"而非"咔"）。
    /// 已实测（Concentus 2.2.2）：连续几帧 PLC 输出后信号自然衰减再回到正常帧，无明显爆音。
    /// </summary>
    public short[] DecodeLost()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodec));
        var pcm = new short[PcmSamplesPerFrame];
        // 空包 + 末位 fec=true（该布尔参数在接口上无命名）：触发 Opus 内置 PLC
        int n = _decoder.Decode(System.Array.Empty<byte>(), pcm, FrameSizeSamples, true);
        if (n < 0) throw new InvalidOperationException($"Opus PLC 解码失败 (code={n})");
        return pcm;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // IOpusEncoder/Decoder 由工厂创建，可能含非托管句柄；
        // 当前 Concentus 2.2.2 公开接口未暴露显式 Dispose。
    }
}
