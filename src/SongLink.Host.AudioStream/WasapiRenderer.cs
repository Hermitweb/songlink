using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SongLink.Host.AudioStream;

/// <summary>
/// WASAPI 渲染器（架构 §14）：把解码后的 16-bit PCM 写入电脑默认输出设备
/// （可把该设备设为 PC 配对的蓝牙音箱 → 实现“手机音频经电脑蓝牙出声”）。
/// 真机使用；CI/无设备下由 NullRenderer 替代。
/// </summary>
public sealed class WasapiRenderer : IAudioRenderer
{
    private BufferedWaveProvider? _buffer;
    private WasapiOut? _out;

    /// <summary>WasapiOut.Volume 用 IAudioEndpointVolume 内部标量(0.0~1.0)。
    /// 我们保留 _volume 缓存以便 Stop→Start 重建后能恢复到上次音量。</summary>
    private double _volume = 1.0;
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            try { if (_out is not null) _out.Volume = (float)_volume; } catch { /* 忽略:可能设备已拔出 */ }
        }
    }

    public void Start(int sampleRate, int channels)
    {
        Stop();
        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        _out = new WasapiOut(AudioClientShareMode.Shared, latency: 60) { Volume = (float)_volume };
        _out.Init(_buffer);
        _out.Play();
    }

    public void WriteShort(ReadOnlySpan<short> pcm)
    {
        var buffer = _buffer;
        if (buffer is null || pcm.IsEmpty) return;
        var bytes = MemoryMarshal.AsBytes(pcm);
        buffer.AddSamples(bytes.ToArray(), 0, bytes.Length);
    }

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _buffer = null;
    }

    public void Dispose() => Stop();
}
