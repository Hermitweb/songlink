using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SongLink.Spikes.AudioSink;

/// <summary>
/// M0 Spike ② · PC 音频接收端（对应架构 §14 / 协议契约 §4）
/// 链路：UDP 收分片 → 解析帧头 → Concentus 解 Opus → NAudio WASAPI 渲染到默认输出设备。
///
/// 用法：
///   dotnet run -- [port]         默认 51000；48kHz/立体声/20ms Opus
///
/// 纯托管栈（无 WinRT），可在任意 Windows 机器编译运行；输出设备=电脑当前默认播放设备
/// （把它配成蓝牙音箱即实现 §14“手机音频经电脑蓝牙出声”）。
/// </summary>
internal static class Program
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FrameMs = 20;
    private static readonly int SamplesPerFrame = SampleRate * FrameMs / 1000; // 960/声道

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0 && args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase))
            return SelfTest();

        int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 51000;
        Console.WriteLine($"[audio-sink] 监听 UDP :{port}  Opus {SampleRate}Hz/{Channels}ch/{FrameMs}ms → WASAPI 默认输出");

        using var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        var waveFormat = new WaveFormat(SampleRate, 16, Channels); // 16-bit PCM，直接喂 Opus 的 short 输出
        var buffer = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        using var outDevice = new WasapiOut(AudioClientShareMode.Shared, latency: 60);
        outDevice.Init(buffer);
        try { outDevice.Play(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[x] 无法启动音频输出（无默认播放设备/被占用？）：{ex.Message}");
            Console.WriteLine("    提示：接上音箱/耳机或把某设备设为默认后重试。");
            return 2;
        }

        using var udp = new UdpClient(port);
        udp.Client.ReceiveBufferSize = 1 << 20;
        IPEndPoint? remote = null;
        long lastSeq = -1, frames = 0, dropped = 0;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.WriteLine("播放中…（Ctrl+C 退出）");

        short[] pcm = new short[SamplesPerFrame * Channels];

        while (!cts.IsCancellationRequested)
        {
            byte[] data;
            try { data = udp.Receive(ref remote); }
            catch (SocketException) { continue; }

            var frame = AudioFrame.Parse(data);
            if (frame is null) continue;

            if (lastSeq >= 0 && frame.Value.Seq > lastSeq + 1)
                dropped += frame.Value.Seq - lastSeq - 1;
            lastSeq = frame.Value.Seq;

            int n = decoder.Decode(frame.Value.Payload.Span, pcm, SamplesPerFrame);
            if (n <= 0) continue;

            // 把 n*Channels 个 short 拷成字节喂给缓冲
            var pcmBytes = MemoryMarshal.AsBytes(pcm.AsSpan(0, n * Channels));
            var chunk = pcmBytes.ToArray();
            buffer.AddSamples(chunk, 0, chunk.Length);
            frames++;

            if (frames % 100 == 0)
                Console.WriteLine($"  frames={frames} seq={frame.Value.Seq} dropped={dropped} buf={buffer.BufferLength}B");
        }

        outDevice.Stop();
        Console.WriteLine($"\n[audio-sink] 结束。共解帧 {frames}，估计丢帧 {dropped}");
        return 0;
    }

    /// <summary>无设备/无网络也能跑的闭环自检：编码→打包→解析→解码，验证帧契约与 Opus 通路。</summary>
    private static int SelfTest()
    {
        Console.WriteLine("[selftest] Opus 编解码 + 帧头契约 闭环验证");
        using var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        using var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // 生成一帧 1kHz 正弦（立体声，int16）
        short[] src = new short[SamplesPerFrame * Channels];
        for (int i = 0; i < SamplesPerFrame; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 1000 * i / SampleRate) * 12000);
            src[i * 2] = v; src[i * 2 + 1] = v;
        }

        byte[] opus = new byte[4000];
        int enc = encoder.Encode(src, SamplesPerFrame, opus, opus.Length);   // 第2参=每声道帧长(960)
        if (enc <= 0) { Console.WriteLine("[selftest] FAIL: 编码返回 0"); return 1; }

        // 按契约 §4 打包：ver|flags|seq(4)|pts(8)|len(2)|payload
        byte[] dgram = new byte[AudioFrame.HeaderLen + enc];
        dgram[0] = 1; dgram[1] = AudioFrame.FlagFirst | AudioFrame.FlagLast;
        BinaryPrimitives.WriteUInt32BigEndian(dgram.AsSpan(2, 4), 1234);
        BinaryPrimitives.WriteUInt64BigEndian(dgram.AsSpan(6, 8), 98765);
        BinaryPrimitives.WriteUInt16BigEndian(dgram.AsSpan(14, 2), (ushort)enc);
        opus.AsSpan(0, enc).CopyTo(dgram.AsSpan(AudioFrame.HeaderLen));

        var f = AudioFrame.Parse(dgram);
        if (f is null) { Console.WriteLine("[selftest] FAIL: 帧解析失败"); return 1; }
        if (f.Value.Seq != 1234 || f.Value.Pts != 98765 || !f.Value.IsFirst || !f.Value.IsLast)
        { Console.WriteLine($"[selftest] FAIL: 帧头字段不符 seq={f.Value.Seq} pts={f.Value.Pts} flags={f.Value.Flags}"); return 1; }

        short[] outPcm = new short[SamplesPerFrame * Channels];
        int n = decoder.Decode(f.Value.Payload.Span, outPcm, SamplesPerFrame);
        double rms = 0; for (int i = 0; i < n * Channels; i++) rms += (double)outPcm[i] * outPcm[i];
        rms = Math.Sqrt(rms / Math.Max(1, n * Channels));

        bool ok = n > 0 && rms > 500;
        Console.WriteLine($"[selftest] 解出样本/声道={n}  RMS={rms:F0}  opusBytes={enc}  → {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
}
