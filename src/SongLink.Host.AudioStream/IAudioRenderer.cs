namespace SongLink.Host.AudioStream;

/// <summary>解码后 PCM 的渲染目标（int16 交织）。测试用 <see cref="NullRenderer"/>，真机用 <see cref="WasapiRenderer"/>。</summary>
public interface IAudioRenderer : IDisposable
{
    void Start(int sampleRate, int channels);
    void WriteShort(ReadOnlySpan<short> pcm);
    void Stop();
}

/// <summary>空渲染器：只计数，不触碰音频设备——用于无显卡/CI 环境的解码闭环测试。</summary>
public sealed class NullRenderer : IAudioRenderer
{
    public long FramesWritten { get; private set; }
    public long SamplesWritten { get; private set; }

    public void Start(int sampleRate, int channels) { }
    public void WriteShort(ReadOnlySpan<short> pcm) { FramesWritten++; SamplesWritten += pcm.Length; }
    public void Stop() { }
    public void Dispose() { }
}
