using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SongLink.Host.AudioStream;

/// <summary>
/// 音频流接收器（契约 §4 PHONE_STREAM + §3.9 audio:start）：
/// 在本地 UDP 端口监听手机端推流 → 按 §4 解析 → Opus 解码 → 喂入 <see cref="IAudioRenderer"/>。
/// 真机用 <see cref="WasapiRenderer"/>（输出到默认声卡，可设为 PC 配对的蓝牙音箱）；
/// 无音频设备的 CI / 单元测试用 <see cref="NullRenderer"/>。
/// 注：本机不主动 push 任何帧给手机，控制流走 §3.9 + Hello/Ack 协议。
/// </summary>
public sealed class AudioStreamSink : IDisposable
{
    private readonly OpusCodec _codec;
    private readonly IAudioRenderer _renderer;

    /// <summary>调整 WASAPI/Null 渲染器的输出音量（0.0~1.0）。Stop 后无效；下次 Start 沿用。</summary>
    public void SetVolume(double volume)
    {
        try { _renderer.Volume = Math.Clamp(volume, 0.0, 1.0); } catch { /* 忽略 */ }
    }
    private readonly int _port;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Task? _loop;

    /// <summary>本机绑定端口（同 §3.9 audio:start.port 协商）。0 = 任意可用端口。</summary>
    public int LocalPort { get; private set; }

    private long _framesReceived;
    private long _framesDecoded;
    private long _framesPcmPassthrough;
    private long _decodeErrors;

    /// <summary>已接收帧计数（不论解码成败）。</summary>
    public long FramesReceived => System.Threading.Interlocked.Read(ref _framesReceived);

    /// <summary>已成功解码并送入渲染器的帧计数（含 Opus 解码和 PCM 直通两条路径）。</summary>
    public long FramesDecoded => System.Threading.Interlocked.Read(ref _framesDecoded);

    /// <summary>PCM 直通模式帧计数（flags bit2=1，未经 OpusCodec）。</summary>
    public long FramesPcmPassthrough => System.Threading.Interlocked.Read(ref _framesPcmPassthrough);

    /// <summary>解码/处理失败次数（用于区分健康度）。</summary>
    public long DecodeErrors => System.Threading.Interlocked.Read(ref _decodeErrors);

    /// <summary>当前激活的渲染模式（基于收到的首帧 flags bit2 判定，启动后不可变）；
    /// null = 还未收到任何帧，沿用 OpusCodec 默认值启动渲染器。</summary>
    public RenderMode? ActiveMode { get; private set; }

    public enum RenderMode { OpusDecoded, RawPcmPassthrough }

    public AudioStreamSink(OpusCodec codec, IAudioRenderer renderer, int port = 0)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _port = port;
    }

    /// <summary>启动 UDP 接收并开启渲染器。同进程可重复调用 (Stop→Start)。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_loop is not null && !_loop.IsCompleted) return;

            _renderer.Start(_codec.SampleRate, _codec.Channels);

            // 0 = 让 OS 分配；显式绑定 IPv4 以避开 Android 推送的 IPv6-only 路径
            _udp = new UdpClient(_port, AddressFamily.InterNetwork);
            // 默认 OS 接收缓冲 ~8 KB 装不下 1 秒 48kHz/mono PCM(≈ 192 KB),Android 端 MediaProjection
            // 高峰帧可能短暂突发超过此值。直接拉到 1 MB 给足余量,避免 ReceiveLoop 频繁空转。
            try { _udp.Client.ReceiveBufferSize = 1 << 20; } catch { /* 忽略:某些 OS 不允许 */ }
            LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => ReceiveLoop(token));
        }
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        var anyRemote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IPEndPoint? remote = anyRemote;
                byte[] datagram;
                try
                {
                    datagram = _udp!.Receive(ref remote);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }

                if (datagram.Length == 0) continue;
                System.Threading.Interlocked.Increment(ref _framesReceived);
                var fr = System.Threading.Interlocked.Read(ref _framesReceived);
                if (fr % 50 == 0)
                {
                    System.Console.WriteLine($"[audio] FramesReceived={fr}, 最近payloadLen={datagram.Length - 16}");
                }

                try
                {
                    var frame = AudioStreamWire.Parse(datagram);
                    if (frame.Version != AudioStreamWire.WireVersion) continue;

                    bool isRawPcm = (frame.Flags & AudioStreamWire.FlagRawPcm) != 0;

                    // 模式锁定：发现帧类型与首帧不一致 → 计入 DecodeErrors 并跳过（不接 metric）
                    if (ActiveMode is { } locked &&
                        ((locked == RenderMode.OpusDecoded && isRawPcm) ||
                         (locked == RenderMode.RawPcmPassthrough && !isRawPcm)))
                    {
                        System.Threading.Interlocked.Increment(ref _decodeErrors);
                        System.Console.Error.WriteLine(
                            $"[audio] 模式锁定={locked}, 但收到 isRawPcm={isRawPcm} 的帧, 跳过");
                        continue;
                    }

                    var pcm = isRawPcm
                        ? DecodeRawPcm(frame.Payload.Span)
                        : _codec.Decode(frame.Payload.Span);
                    if (pcm.Length == 0) continue;

                    // 首次遇到 PCM 直通帧时把渲染器按 PCM 重新拉起（Android 端送 48k/mono，
                    // 而 OpusCodec 配置可能不同）。一旦确定模式就锁死。
                    EnsureRendererFor(isRawPcm);

                    _renderer.WriteShort(pcm);
                    System.Threading.Interlocked.Increment(ref _framesDecoded);
                    if (isRawPcm) System.Threading.Interlocked.Increment(ref _framesPcmPassthrough);
                }
                catch (Exception ex)
                {
                    // 协议层或解码失败不应中断接收循环——计入 metrics 即可
                    System.Threading.Interlocked.Increment(ref _decodeErrors);
                    System.Console.Error.WriteLine($"[audio] 解码失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested is false)
        {
            System.Console.Error.WriteLine($"[audio] 接收循环异常: {ex.Message}");
        }
    }

    /// <summary>仅停止渲染器（保留 UDP 句柄），用于调试/暂停。</summary>
    public void Stop()
    {
        _renderer.Stop();
    }

    private void EnsureRendererFor(bool rawPcm)
    {
        var target = rawPcm ? RenderMode.RawPcmPassthrough : RenderMode.OpusDecoded;
        if (ActiveMode is { } current && current != target) return;   // 模式锁死后不再切换
        if (ActiveMode is null)
        {
            // 首次按真实负载类型重置渲染器——避免 48k/2ch Opus 渲染器上灌 48k/1ch PCM
            _renderer.Start(
                sampleRate: rawPcm ? AudioStreamWire.RawPcmSampleRate : _codec.SampleRate,
                channels:   rawPcm ? AudioStreamWire.RawPcmChannels   : _codec.Channels);
            ActiveMode = target;
        }
    }

    private static short[] DecodeRawPcm(ReadOnlySpan<byte> payload)
    {
        // Android 端 ENCODING_PCM_16BIT = little-endian 16-bit signed PCM；
        // 字节数必须是 2 的倍数；按 short[] 直接拷贝（小端机器上 layout 一致，Intel x86 / x64 不需要 swap）。
        if ((payload.Length & 1) != 0) throw new InvalidOperationException($"PCM 载荷长度必须是 2 的倍数, 实际 {payload.Length}");
        var samples = new short[payload.Length / 2];
        if (BitConverter.IsLittleEndian)
        {
            Buffer.BlockCopy(payload.ToArray(), 0, samples, 0, payload.Length);
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)((payload[2 * i]) | (payload[2 * i + 1] << 8));
        }
        return samples;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _cts?.Cancel();
                _udp?.Close();
                _udp?.Dispose();
                _loop = null;
            }
            catch { /* 忽略关闭异常 */ }
            finally
            {
                _cts?.Dispose(); _cts = null;
                _udp = null;
            }
            try { _renderer.Dispose(); } catch { }
            try { _codec.Dispose(); } catch { }
        }
    }
}
