using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using SongLink.Protocol;

namespace SongLink.Host.AudioStream;

/// <summary>
/// M2.5: 音频流接收器（UDP/WebSocket双通道 + Opus 解码 + WASAPI输出）
/// </summary>
public sealed class AudioStreamSink : IAudioRenderer, IDisposable
{
    private readonly OpusCodec _codec;
    private readonly CancellationTokenSource _cts = new();
    
    private UdpClient? _udpClient;
    private ClientWebSocket? _wsClient;
    private Task? _udpTask;
    private Task? _wsTask;
    
    public int CurrentSampleRate { get; private set; }
    public int CurrentChannels { get; private set; }
    
    public AudioStreamSink(OpusCodec codec)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }
    
    public void Start(int sampleRate, int channels)
    {
        CurrentSampleRate = sampleRate;
        CurrentChannels = channels;
        
        Console.WriteLine($"[audio] Start: {sampleRate}Hz {channels}ch");
        
        // 这里可以启动 UDP 或 WS 监听器
        
        if (_udpClient is null && _wsClient is null)
        {
            Console.WriteLine("[audio] 等待启动 UDP 或 WebSocket 监听...");
        }
    }
    
    public void WriteShort(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return;
        
        Console.WriteLine($"[audio] Received {pcm.Length / CurrentChannels * 2} bytes of PCM data");
        // TODO: 实际实现需要将数据写入音频设备
    }
    
    public void Stop()
    {
        _cts.Cancel();
        
        if (_udpTask is not null) { try { _udpTask.Wait(1000); } catch { } }
        if (_wsTask is not null) { try { _wsTask.Wait(1000); } catch { } }
        
        _udpClient?.Close();
        _udpClient?.Dispose();
        _wsClient?.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", _cts.Token).GetAwaiter().GetResult();
        
        Console.WriteLine("[audio] Audio stream stopped");
    }
    
    public void Dispose()
    {
        Stop();
        _codec.Dispose();
        _cts.Dispose();
    }
}
