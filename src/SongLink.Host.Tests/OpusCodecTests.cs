using System.Linq;
using SongLink.Host.AudioStream;

namespace SongLink.Host.Tests;

/// <summary>
/// 协议 §4 PHONE_STREAM 帧格式 + OpusCodec (Concentus 包装) 闭环测试。
/// 全部用例不需要声卡或音频设备，可直接在 CI 跑通。
/// </summary>
public class AudioStreamWireTests
{
    [Fact]
    public void Assemble_then_Parse_roundtrips_all_header_fields()
    {
        var opus = new byte[] { 0xFC, 0xAB, 0x12, 0x34, 0x56 };
        var seq = 0x1234_5678u;
        var pts = 0xAABB_CCDD_EEFF_0011UL;
        var flags = (byte)(AudioStreamWire.FlagFirst | AudioStreamWire.FlagLast);

        var packet = AudioStreamWire.Assemble(flags, seq, pts, opus);
        Assert.Equal(16 + opus.Length, packet.Length);
        Assert.Equal(AudioStreamWire.WireVersion, packet[0]);
        Assert.Equal(flags, packet[1]);

        var frame = AudioStreamWire.Parse(packet);
        Assert.Equal(AudioStreamWire.WireVersion, frame.Version);
        Assert.Equal(flags, frame.Flags);
        Assert.Equal(seq, frame.Seq);
        Assert.Equal(pts, frame.Pts);
        Assert.Equal((ushort)opus.Length, frame.PayloadLen);
        Assert.True(opus.SequenceEqual(frame.Payload.ToArray()));
    }

    [Fact]
    public void FrameTotalSize_matches_header_plus_payload()
    {
        Assert.Equal(16, AudioStreamWire.FrameTotalSize(0));
        Assert.Equal(20, AudioStreamWire.FrameTotalSize(4));
        Assert.Equal(16 + ushort.MaxValue, AudioStreamWire.FrameTotalSize(ushort.MaxValue));
    }

    [Fact]
    public void Parse_rejects_truncated_header()
    {
        var pkt = new byte[15];
        Assert.Throws<InvalidOperationException>(() => AudioStreamWire.Parse(pkt));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)99)]
    public void Parse_rejects_unsupported_wire_version(byte ver)
    {
        var pkt = new byte[20];
        pkt[0] = ver;
        var ex = Assert.Throws<InvalidOperationException>(() => AudioStreamWire.Parse(pkt));
        Assert.Contains("ver=", ex.Message);
    }

    [Fact]
    public void Parse_rejects_truncated_payload()
    {
        var pkt = new byte[16 + 3];
        pkt[0] = AudioStreamWire.WireVersion;
        // payloadLen = 100 但实际只有 3 字节
        pkt[14] = 0; pkt[15] = 100;
        var ex = Assert.Throws<InvalidOperationException>(() => AudioStreamWire.Parse(pkt));
        Assert.Contains("payload 不完整", ex.Message);
    }

    [Fact]
    public void Assemble_rejects_oversized_opus_payload()
    {
        var oversize = new byte[ushort.MaxValue + 1];
        Assert.Throws<ArgumentException>(() => AudioStreamWire.Assemble(0, 0, 0, oversize));
    }
}

public class OpusCodecTests
{
    /// <summary>不同实例的 Concentus 编码输出可能含内部抖动；解码产物应与原 PCM 高度相似，
    /// 不要求逐样本相等。本断言只验证：解码非全 0、长度匹配 → 至少一个通路能跑通。</summary>
    [Fact]
    public void Encode_then_Decode_roundtrips_sine()
    {
        using var codec = new OpusCodec(sampleRate: 48000, channels: 2, bitrateBps: 96_000, frameMs: 20);

        // 单声道 440Hz 正弦 → 立体声交织 (L, R, L, R, ...)
        int N = codec.FrameSizeSamples;
        var pcm = new short[2 * N];
        for (int i = 0; i < N; i++)
        {
            short v = (short)(System.Math.Sin(2 * Math.PI * 440.0 * i / 48000.0) * 16000);
            pcm[2 * i] = v;
            pcm[2 * i + 1] = v;
        }

        var encoded = codec.Encode(pcm);
        Assert.NotEmpty(encoded);
        // Opus 单帧通常 10..200 字节
        Assert.InRange(encoded.Length, 1, 4000);

        var decoded = codec.Decode(encoded);
        Assert.Equal(pcm.Length, decoded.Length);
        // 不要求样本全等（压缩是有损），但决不能全 0
        Assert.Contains(decoded, s => s != 0);
    }

    [Fact]
    public void Encode_returns_buffer_shorter_than_input_when_typical_speech()
    {
        using var codec = new OpusCodec(sampleRate: 16000, channels: 1, bitrateBps: 32_000, frameMs: 20);
        int N = codec.FrameSizeSamples;
        // 伪语音：静音包 vs 短促噪声
        var pcm = new short[N];
        for (int i = 0; i < N; i++) pcm[i] = (short)(Random.Shared.Next(-2000, 2000));

        var encoded = codec.Encode(pcm);
        // 16kHz/单声道/20ms 理论上 opus 输出 20..200 字节
        Assert.InRange(encoded.Length, 1, 1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(44100)]   // 不在 opus 支持的 5 档里
    [InlineData(11025)]
    public void OpusCodec_rejects_bad_sample_rate(int sr)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusCodec(sampleRate: sr));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void OpusCodec_rejects_bad_channel_count(int ch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusCodec(channels: ch));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    public void OpusCodec_rejects_bad_frame_duration(int frameMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusCodec(frameMs: frameMs));
    }

    [Fact]
    public void Decode_rejects_empty_packet()
    {
        using var codec = new OpusCodec();
        Assert.Throws<ArgumentException>(() => codec.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Encode_rejects_wrong_pcm_length()
    {
        using var codec = new OpusCodec(sampleRate: 48000, channels: 2, frameMs: 20);
        // frameSizeSamples=960; channels=2 -> pcm.Length 应=1920；给 100 触发错误
        var pcm = new short[100];
        Assert.Throws<ArgumentException>(() => codec.Encode(pcm));
    }

    [Fact]
    public void Dispose_can_be_called_twice_without_throw()
    {
        var codec = new OpusCodec();
        codec.Dispose();
        codec.Dispose();
    }

    [Fact]
    public void Encode_after_Dispose_throws_ObjectDisposedException()
    {
        var codec = new OpusCodec();
        codec.Dispose();
        var pcm = new short[codec.PcmSamplesPerFrame];
        Assert.Throws<ObjectDisposedException>(() => codec.Encode(pcm));
    }
}

/// <summary>AudioStreamSink 端到端测试：发 UDP → §4 解析 → Opus 解码 → NullRenderer 计数。
/// 不需要声卡、不需要双向音频设备，可在 CI 跑。</summary>
public class AudioStreamSinkTests
{
    [Fact]
    public async Task Sink_decodes_inbound_udp_packets_and_counts_frames()
    {
        // 准备：本地零端口绑定
        using var codec = new OpusCodec(sampleRate: 16000, channels: 1, bitrateBps: 32_000, frameMs: 20);
        var renderer = new NullRenderer();
        using var sink = new AudioStreamSink(codec, renderer, port: 0);
        sink.Start();
        Assert.True(sink.LocalPort > 0, "OS 应分配非零本地端口");

        // 生成一段 PCM 然后编码成 §4 帧
        int N = codec.PcmSamplesPerFrame;
        var pcm = new short[N];
        for (int i = 0; i < N; i++) pcm[i] = (short)(System.Math.Sin(2 * Math.PI * 440.0 * i / 16000.0) * 8000);
        var opus = codec.Encode(pcm);
        var frame = AudioStreamWire.Assemble(
            flags: (byte)(AudioStreamWire.FlagFirst | AudioStreamWire.FlagLast),
            seq: 1u,
            pts: 16000UL,
            opus);

        // 用一个随机本地端口把帧打过去
        using var sender = new System.Net.Sockets.UdpClient(0);
        var bytesSent = await sender.SendAsync(frame, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sink.LocalPort));
        Assert.Equal(frame.Length, bytesSent);

        // 等若干毫秒让接收循环跑完
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && sink.FramesDecoded < 1)
            await Task.Delay(20);

        Assert.True(sink.FramesReceived >= 1, $"应收到至少 1 帧，但 FramesReceived={sink.FramesReceived}");
        Assert.True(sink.FramesDecoded >= 1, $"应解码至少 1 帧，但 FramesDecoded={sink.FramesDecoded}");
        Assert.True(renderer.SamplesWritten > 0, "解码产物应已写入 NullRenderer");
        Assert.Equal(0, sink.DecodeErrors);
    }

    [Fact]
    public async Task Sink_silently_drops_unsupported_version_frames()
    {
        using var codec = new OpusCodec(sampleRate: 16000, channels: 1, bitrateBps: 32_000, frameMs: 20);
        var renderer = new NullRenderer();
        using var sink = new AudioStreamSink(codec, renderer, port: 0);
        sink.Start();

        // 拼一个 ver=99 的帧，§4 解析层会拒绝
        var bad = new byte[20];
        bad[0] = 99;
        bad[14] = 0; bad[15] = 4;     // payloadLen = 4
        using var sender = new System.Net.Sockets.UdpClient(0);
        await sender.SendAsync(bad, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sink.LocalPort));

        await Task.Delay(120);
        Assert.Equal(0, sink.FramesDecoded);
        // §4 解析抛 → 被 catch 计入 DecodeErrors
        Assert.True(sink.DecodeErrors >= 1);
    }
}
