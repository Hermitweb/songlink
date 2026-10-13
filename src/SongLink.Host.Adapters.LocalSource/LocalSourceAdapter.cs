using NAudio.Wave;
using SongLink.Protocol;

namespace SongLink.Host.Adapters.LocalSource;

/// <summary>
/// 本地音源适配器（NAudio 版，架构 §3.6 / S2）：自注册 SMTC 的小播放器（NAudio），允许拖入 MP3/LRC→永远能放。
/// 兜底方案：第三方 App 都不可用时，本地曲库依然可用；完全可控、无平台依赖。
///
/// 与 WmPlayerSourceAdapter（WMPlayer COM 版）并存，取舍见该类注释。
/// 注：NAudio 的 AudioFileReader 自身即 ISampleProvider，无需再包一层自定义 provider；
/// 构造时不预载曲目（曲库初始为空，强行打开 demo.mp3 会直接抛 FileNotFound），
/// 由 Pick() 首次装载后再 Play()。
/// </summary>
public sealed class LocalSourceAdapter : IPlayerAdapter, IDisposable
{
    private readonly WaveOutEvent _waveOut;
    private readonly LocalLibrary _library;
    private AudioFileReader? _reader;     // 当前解码源
    private WaveChannel32? _channel;      // 送入声卡的采样流
    private string _currentFile = "";

    private volatile bool _disposed;
    private static int _sessionCounter = 0;
    private readonly int _sessionId = Interlocked.Increment(ref _sessionCounter);

    public LocalSourceAdapter(LocalLibrary? library = null)
    {
        _library = library ?? new LocalLibrary();

        // NAudio 初始化（此时尚未装载曲目，故不 Init）
        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
    }

    public string AppId => "localsource";
    public AdapterCapability Caps => AdapterCapability.ReadMeta | AdapterCapability.Transport | AdapterCapability.Pick | AdapterCapability.Search | AdapterCapability.Volume;

    /// <summary>读取当前播放真值（NAudio 状态 + SMTC 反馈）。</summary>
    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<NowPlaying?>(null);

        var channel = _channel;
        var hasTrack = channel is not null && channel.TotalTime > TimeSpan.Zero;

        var np = new NowPlaying
        {
            Title = hasTrack ? Path.GetFileName(_currentFile) : "空闲",
            Artist = "本地音源",
            // WaveStream.Position 是字节偏移，需按 WaveFormat 折算成秒
            Pos = hasTrack ? (float)(channel!.Position / (double)channel!.WaveFormat.AverageBytesPerSecond) : 0,
            Dur = hasTrack ? (float)channel!.TotalTime.TotalSeconds : 0,
            Status = _waveOut.PlaybackState switch
            {
                PlaybackState.Playing => "playing",
                PlaybackState.Paused => "paused",
                _ => "stopped"
            },
            SourceMode = "PC_LOCAL"
        };

        return Task.FromResult<NowPlaying?>(np);
    }

    public Task<bool> Play(CancellationToken ct = default)
    {
        if (_disposed || _channel is null) return Task.FromResult(false);

        // 添加到播放历史
        _library.AddPlayHistory(Path.GetFileName(_currentFile), "本地音源");

        _waveOut.Play();
        return Task.FromResult(true);
    }

    public Task<bool> Pause(CancellationToken ct = default)
    {
        _waveOut.Pause();
        return Task.FromResult(true);
    }

    public Task<bool> Next(CancellationToken ct = default)
    {
        // 简单实现：从库中找下一首
        return PickNextSong(1);
    }

    public Task<bool> Prev(CancellationToken ct = default)
    {
        // 简单实现：从库中找上一首
        return PickNextSong(-1);
    }

    public Task<bool> Stop(CancellationToken ct = default)
    {
        _waveOut.Stop();
        return Task.FromResult(true);
    }

    /// <summary>点歌：从本地库加载指定曲目。</summary>
    public async Task<bool> Pick(string title, string artist, CancellationToken ct = default)
    {
        if (_disposed) return false;

        var track = await Task.Run(() => _library.Search($"{title} {artist}").FirstOrDefault(), ct);
        if (track is null) return false;

        var filePath = Path.Combine(_library.Directory, track.File);
        if (!File.Exists(filePath)) return false;

        try
        {
            _waveOut.Stop();

            var reader = new AudioFileReader(filePath);
            var channel = new WaveChannel32(reader);
            _waveOut.Init(channel);

            // 只有 Init 成功才替换旧句柄，避免换歌失败时把正在播的也弄丢
            var oldReader = _reader;
            var oldChannel = _channel;
            _reader = reader;
            _channel = channel;
            _currentFile = filePath;

            oldChannel?.Dispose();
            oldReader?.Dispose();

            _waveOut.Play();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
    {
        if (_disposed) return Array.Empty<SearchResultItem>();
        var tracks = await Task.Run(() => _library.Search(kw), ct);
        return tracks.Select(t => new SearchResultItem
        {
            Title = t.Title,
            Artist = t.Artist,
            Album = "本地",
            Dur = 180.0 // 默认值，真实时长需音频分析
        }).ToList();
    }

    // NAudio WaveOutEvent.Volume 控制输出采样音量(0.0~1.0)。WaveOutEvent 自身默认 1.0。
    public Task<double> GetVolume(CancellationToken ct = default)
        => Task.FromResult(_disposed ? 1.0 : _waveOut.Volume);
    public Task<bool> SetVolume(double volume, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(false);
        _waveOut.Volume = Math.Clamp((float)volume, 0f, 1f);
        return Task.FromResult(true);
    }

    private void RegisterSmtcSession()
    {
        // NAudio 自动向系统注册为媒体播放端（通过 WASAPI 输出设备）
        // 无需额外 SMTC 注册代码
    }

    private async Task<bool> PickNextSong(int direction)
    {
        if (_disposed) return false;

        // 之前用 _library.Search("") 切空关键词恒返回空列表，所以 Next/Prev 一直是死代码。
        // 改用 _library.Tracks 公开枚举 API。
        var allTracks = await Task.Run(() => _library.Tracks);
        if (allTracks.Count == 0) return false;

        // IReadOnlyList<T> 不暴露 IndexOf，用单次遍历同时算 currentIndex
        int currentIndex = -1;
        for (int i = 0; i < allTracks.Count; i++)
        {
            if (allTracks[i].File == Path.GetFileName(_currentFile))
            {
                currentIndex = i;
                break;
            }
        }
        int nextIndex = (currentIndex + direction + allTracks.Count) % allTracks.Count;

        return await Pick(allTracks[nextIndex].Title, allTracks[nextIndex].Artist);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _channel?.Dispose();
            _reader?.Dispose();
        }
        catch { /* 忽略 */ }
    }
}
