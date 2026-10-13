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
    private readonly int _port;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Task? _loop;

    /// <summary>本机绑定端口（同 §3.9 audio:start.port 协商）。0 = 任意可用端口。</summary>
    public int LocalPort { get; private set; }

    private long _framesReceived;
    private long _framesDecoded;
    private long _decodeErrors;

    /// <summary>已接收帧计数（不论解码成败）。</summary>
    public long FramesReceived => System.Threading.Interlocked.Read(ref _framesReceived);

    /// <summary>已成功解码并送入渲染器的帧计数。</summary>
    public long FramesDecoded => System.Threading.Interlocked.Read(ref _framesDecoded);

    /// <summary>解码失败次数（用于区分健康度）。</summary>
    public long DecodeErrors => System.Threading.Interlocked.Read(ref _decodeErrors);

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

                try
                {
                    var frame = AudioStreamWire.Parse(datagram);
                    if (frame.Version != AudioStreamWire.WireVersion) continue;

                    var pcm = _codec.Decode(frame.Payload.Span);
                    if (pcm.Length == 0) continue;

                    _renderer.WriteShort(pcm);
                    System.Threading.Interlocked.Increment(ref _framesDecoded);
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
