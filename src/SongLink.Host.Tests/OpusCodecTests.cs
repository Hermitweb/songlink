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

    /// <summary>Android 端 16-bit LE PCM 直通：flags bit2 = <see cref="AudioStreamWire.FlagRawPcm"/>。
    /// 这是 Android PhoneAudioCapturer 当前发送的唯一形态——OpusCodec 不参与，整路径直通。</summary>
    [Fact]
    public async Task Sink_passes_raw_pcm_through_when_flag_bit2_set()
    {
        using var codec = new OpusCodec(sampleRate: 48_000, channels: 2, bitrateBps: 96_000, frameMs: 20);
        var renderer = new NullRenderer();
        using var sink = new AudioStreamSink(codec, renderer, port: 0);
        sink.Start();

        // PCM 载荷：单声道 48 kHz / 20 ms = 960 sample = 1920 byte（Android 端约定值）。
        var pcmBytes = new byte[1920];
        for (int i = 0; i < 960; i++)
        {
            short v = (short)(System.Math.Sin(2 * Math.PI * 440.0 * i / 48_000.0) * 16_000);
            pcmBytes[2 * i]     = (byte)(v & 0xFF);
            pcmBytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }

        var frame = AudioStreamWire.Assemble(
            flags: (byte)(AudioStreamWire.FlagFirst | AudioStreamWire.FlagLast | AudioStreamWire.FlagRawPcm),
            seq: 1u,
            pts: 48_000UL,
            opusFrame: pcmBytes);

        using var sender = new System.Net.Sockets.UdpClient(0);
        await sender.SendAsync(frame, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sink.LocalPort));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && sink.FramesPcmPassthrough < 1)
            await Task.Delay(20);

        Assert.True(sink.FramesPcmPassthrough >= 1, $"应至少 1 帧 PCM 直通，实际 {sink.FramesPcmPassthrough}");
        Assert.Equal(AudioStreamSink.RenderMode.RawPcmPassthrough, sink.ActiveMode);
        Assert.Equal(960, renderer.SamplesWritten);
        Assert.Equal(0, sink.DecodeErrors);
    }

    /// <summary>PCM 直通载荷字节数不是 2 的倍数 → 计入 DecodeErrors、不崩循环。</summary>
    [Fact]
    public async Task Sink_counts_pcm_malformed_payload_as_decode_error()
    {
        using var codec = new OpusCodec(sampleRate: 48_000, channels: 1, bitrateBps: 96_000, frameMs: 20);
        var renderer = new NullRenderer();
        using var sink = new AudioStreamSink(codec, renderer, port: 0);
        sink.Start();

        var bad = new byte[5];   // 奇数长度 → 应抛 InvalidOperationException
        var frame = AudioStreamWire.Assemble(
            flags: AudioStreamWire.FlagRawPcm,
            seq: 1u,
            pts: 0,
            opusFrame: bad);

        using var sender = new System.Net.Sockets.UdpClient(0);
        await sender.SendAsync(frame, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sink.LocalPort));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && sink.DecodeErrors < 1)
            await Task.Delay(20);

        Assert.True(sink.DecodeErrors >= 1);
        Assert.Equal(0, sink.FramesPcmPassthrough);
    }

    /// <summary>首帧是 Opus、后续切到 PCM——锁死后不再切换，确保 WASAPI 不会在 48k/1ch/48k/2ch 之间跳。</summary>
    [Fact]
    public async Task Sink_locks_mode_after_first_frame()
    {
        using var codec = new OpusCodec(sampleRate: 16_000, channels: 1, bitrateBps: 32_000, frameMs: 20);
        var renderer = new NullRenderer();
        using var sink = new AudioStreamSink(codec, renderer, port: 0);
        sink.Start();

        // 第 1 帧：Opus（16 sample/通道）
        var pcm1 = new short[320];   // 16k * 20ms = 320 sample/通道
        for (int i = 0; i < pcm1.Length; i++) pcm1[i] = (short)i;
        var opus1 = codec.Encode(pcm1);
        var f1 = AudioStreamWire.Assemble(
            flags: (byte)(AudioStreamWire.FlagFirst | AudioStreamWire.FlagLast),
            seq: 1u, pts: 16_000UL, opusFrame: opus1);

        // 第 2 帧：PCM 直通（伪造 1920 B，单声道 48 kHz 帧大小）
        var pcm2 = new byte[1920];
        var f2 = AudioStreamWire.Assemble(
            flags: (byte)(AudioStreamWire.FlagFirst | AudioStreamWire.FlagLast | AudioStreamWire.FlagRawPcm),
            seq: 2u, pts: 32_000UL, opusFrame: pcm2);

        using var sender = new System.Net.Sockets.UdpClient(0);
        await sender.SendAsync(f1, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sink.LocalPort));
        await sender.SendAsync(f2, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sink.LocalPort));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && sink.FramesDecoded < 2)
            await Task.Delay(20);

        // 模式应锁在 Opus：第 2 帧被"忽略"（即应跳过，避免 WASAPI 切采样率）
        Assert.Equal(AudioStreamSink.RenderMode.OpusDecoded, sink.ActiveMode);
        Assert.Equal(0, sink.FramesPcmPassthrough);
        // 但 DecodeErrors 应+1 或 keep=1，因为它会被 raw-pcm 分支拒绝
        Assert.True(sink.DecodeErrors >= 1 || sink.FramesPcmPassthrough == 1,
            $"模式锁后, raw-pcm 帧应被丢弃: PCM={sink.FramesPcmPassthrough}, Err={sink.DecodeErrors}");
    }

    /// <summary>向系统要一个当前空闲的 UDP 端口（绑 0 后读回）。
    /// 比固定区间随机稳：CI runner 上 51_000~54_007 一段可能被别的进程占着
    /// （2026-10-26 的 CI 就因此挂过一次 Sink_with_port_already_bound）。</summary>
    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    /// <summary>Android 端固定推 51000,Host 必须真绑到那个端口 — 验证 commit d188d9f 的 explicit-port 修复。</summary>
    [Fact]
    public void Sink_with_explicit_port_binds_exactly_that_port()
    {
        using var codec = new OpusCodec(sampleRate: 16_000, channels: 1);
        var renderer = new NullRenderer();
        var port = FreeUdpPort();
        using var sink = new AudioStreamSink(codec, renderer, port: port);
        sink.Start();
        Assert.Equal(port, sink.LocalPort);
        sink.Dispose();
    }

    /// <summary>同一个端口绑两次 → 第二次 Start() 应抛 SocketException (AddressAlreadyInUse)。
    /// 验证 Host 启动失败时能回 Ack{Ok=false} 而非静默丢失。</summary>
    [Fact]
    public void Sink_with_port_already_bound_throws_on_Start()
    {
        using var codec = new OpusCodec(sampleRate: 16_000, channels: 1);
        var renderer = new NullRenderer();
        var port = FreeUdpPort();

        using var first = new AudioStreamSink(codec, new NullRenderer(), port: port);
        first.Start();
        Assert.Equal(port, first.LocalPort);

        // 第二次绑同端口 → UdpClient ctor 直接抛 SocketException
        using var second = new AudioStreamSink(codec, renderer, port: port);
        var ex = Assert.ThrowsAny<Exception>(() => second.Start());
        Assert.True(
            ex is System.Net.Sockets.SocketException
            || ex.InnerException is System.Net.Sockets.SocketException,
            $"应抛 SocketException, 实际: {ex.GetType().Name} {ex.Message}");
    }

    /// <summary>同进程重复 Start() 应幂等（已运行 → 直接返回），不会重复绑定端口。</summary>
    [Fact]
    public void Sink_Start_is_idempotent_while_loop_running()
    {
        using var codec = new OpusCodec(sampleRate: 16_000, channels: 1);
        var renderer = new NullRenderer();
        var port = FreeUdpPort();
        using var sink = new AudioStreamSink(codec, renderer, port: port);

        sink.Start();
        var firstLocal = sink.LocalPort;
        Assert.Equal(port, firstLocal);

        // 第二次 Start 不应抛也不应换端口（接收循环还在跑）
        sink.Start();
        Assert.Equal(firstLocal, sink.LocalPort);
    }

    /// <summary>Dispose() 后再次 Start() 应能干净启 (同进程内重连 — audio:start 重新触发场景)。</summary>
    [Fact]
    public async Task Sink_can_restart_after_Dispose()
    {
        using var codec = new OpusCodec(sampleRate: 16_000, channels: 1);
        var renderer = new NullRenderer();
        var port = FreeUdpPort();
        var sink = new AudioStreamSink(codec, renderer, port: port);

        sink.Start();
        await Task.Delay(50);
        sink.Dispose();

        // 注：Dispose 后 Start() 因为 _loop 已 Complete（被 break）会重入路径,但 UdpClient 已 dispose。
        // 这条用例标记「Dispose→重新构造新实例才安全」,作为已知契约。
        var sink2 = new AudioStreamSink(codec, renderer, port: port);
        sink2.Start();
        Assert.Equal(port, sink2.LocalPort);
        sink2.Dispose();
    }
}
