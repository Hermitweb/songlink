using System.Net.Sockets;
using Concentus;
using Concentus.Enums;
using SongLink.Host.AudioStream;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>音频串流并入 Host 的回路测试（不依赖声卡：NullRenderer）。验证 §4 帧头 + UDP→解码闭环。</summary>
public class AudioStreamTests
{
    [Fact]
    public void AudioFrame_pack_parse_roundtrip()
    {
        byte[] payload = { 1, 2, 3, 4, 5 };
        var dgram = AudioFrame.Pack(AudioFrame.FlagFirst | AudioFrame.FlagLast, 1234, 98765, payload);
        var f = AudioFrame.Parse(dgram);
        Assert.NotNull(f);
        Assert.Equal(1234u, f!.Value.Seq);
        Assert.Equal(98765ul, f.Value.Pts);
        Assert.True(f.Value.IsFirst && f.Value.IsLast);
        Assert.Equal(payload, f.Value.Payload.ToArray());
    }

    [Fact]
    public void AudioFrame_parse_rejects_bad_packets()
    {
        Assert.Null(AudioFrame.Parse(new byte[10]));                 // 太短
        var badVer = AudioFrame.Pack(0, 1, 1, new byte[] { 9 }); badVer[0] = 2;
        Assert.Null(AudioFrame.Parse(badVer));                       // 版本不符
        var truncated = AudioFrame.Pack(0, 1, 1, new byte[] { 9 });
        BinaryPrimitivesWrite(truncated, (ushort)999);
        Assert.Null(AudioFrame.Parse(truncated));                    // 声明长度越界
    }

    private static void BinaryPrimitivesWrite(byte[] d, ushort len)
    {
        d[14] = (byte)(len >> 8); d[15] = (byte)len;
    }

    [Fact]
    public void Sink_decodes_udp_opus_loopback()
    {
        int port = 51800 + Random.Shared.Next(300);
        var nullr = new NullRenderer();
        using var sink = new AudioStreamSink(nullr, 48000, 2, 20);
        sink.Start(port);

        using var enc = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        using var udp = new UdpClient();
        short[] pcm = new short[960 * 2];
        byte[] outb = new byte[4000];

        for (int i = 0; i < 6; i++)
        {
            for (int s = 0; s < 960; s++)
            {
                short v = (short)(Math.Sin(2 * Math.PI * 440 * s / 48000) * 10000);
                pcm[s * 2] = v; pcm[s * 2 + 1] = v;
            }
            int n = enc.Encode(pcm, 960, outb, outb.Length);
            var dgram = AudioFrame.Pack((byte)(AudioFrame.FlagFirst | AudioFrame.FlagLast), (uint)i, (ulong)(i * 960), outb.AsSpan(0, n));
            udp.Send(dgram, dgram.Length, "127.0.0.1", port);
            Thread.Sleep(25);
        }

        var dl = DateTime.UtcNow.AddSeconds(2);
        while (sink.DecodedFrames < 5 && DateTime.UtcNow < dl) Thread.Sleep(50);
        sink.Stop();

        Assert.True(sink.DecodedFrames >= 5, $"解码帧数={sink.DecodedFrames}（期望≥5）");
        Assert.True(nullr.SamplesWritten > 0, "渲染器应收到非空 PCM");
        Assert.True(sink.Dropped == 0, $"不应丢帧，实际 dropped={sink.Dropped}");
    }
}
