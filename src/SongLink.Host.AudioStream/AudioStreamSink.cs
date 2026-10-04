using System.Net;
using System.Net.Sockets;
using Concentus;
using Concentus.Enums;
using SongLink.Protocol;

namespace SongLink.Host.AudioStream;

/// <summary>
/// 手机音频串流接收端（架构 §14 / 契约 §4）：UDP 收分片 → AudioFrame 解析 → Concentus 解 Opus → IAudioRenderer 渲染。
/// 由 HostServer 在 audio:start/stop 时启停；PHONE_STREAM 下把手机音频经电脑（可蓝牙）出声。
/// 解码与渲染解耦：CI/无声卡环境注入 <see cref="NullRenderer"/> 即可验证解码闭环。
/// </summary>
public sealed class AudioStreamSink : IDisposable
{
    private readonly IAudioRenderer _renderer;
    private readonly int _sampleRate, _channels, _frameMs;
    private readonly int _samplesPerFrame;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private UdpClient? _udp;
    private IOpusDecoder? _decoder;
    private long _lastSeq = -1;

    public long DecodedFrames { get; private set; }
    public long Dropped { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };

    public AudioStreamSink(IAudioRenderer? renderer = null, int sampleRate = 48000, int channels = 2, int frameMs = 20)
    {
        _renderer = renderer ?? new WasapiRenderer();
        _sampleRate = sampleRate; _channels = channels; _frameMs = frameMs;
        _samplesPerFrame = sampleRate * frameMs / 1000;
    }

    public void Start(int udpPort)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _decoder = OpusCodecFactory.CreateDecoder(_sampleRate, _channels);
        _renderer.Start(_sampleRate, _channels);
        _udp = new UdpClient(udpPort);
        _udp.Client.ReceiveBufferSize = 1 << 20;
        _lastSeq = -1;
        _loop = Task.Run(() => ReceiveLoopAsync(_udp, _decoder, _cts.Token));
    }

    private async Task ReceiveLoopAsync(UdpClient udp, IOpusDecoder decoder, CancellationToken ct)
    {
        short[] pcm = new short[_samplesPerFrame * _channels];
        while (!ct.IsCancellationRequested)
        {
            byte[] data;
            try { data = (await udp.ReceiveAsync(ct)).Buffer; }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var frame = AudioFrame.Parse(data);
            if (frame is null) { Dropped++; continue; }

            if (_lastSeq >= 0 && frame.Value.Seq > _lastSeq + 1)
                Dropped += frame.Value.Seq - _lastSeq - 1;   // 丢帧统计（Opus PLC 兜底）
            _lastSeq = frame.Value.Seq;

            try
            {
                int n = decoder.Decode(frame.Value.Payload.Span, pcm, _samplesPerFrame);
                if (n > 0) { _renderer.WriteShort(pcm.AsSpan(0, n * _channels)); DecodedFrames++; }
            }
            catch { Dropped++; }
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _loop?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        _renderer.Stop();
        _loop = null; _udp = null; _cts = null; _decoder = null;
    }

    public void Dispose()
    {
        Stop();
        _renderer.Dispose();
    }
}
