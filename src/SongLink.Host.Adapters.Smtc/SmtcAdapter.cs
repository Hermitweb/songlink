using Windows.Foundation;
using Windows.Media.Control;
using SongLink.Protocol;

namespace SongLink.Host.Adapters.Smtc;

/// <summary>
/// SMTC 通用适配器（架构 §6.1 梯队 1）：读系统"正在播放"会话的元数据/进度/封面，并下发传输控制。
/// 跨 App 稳定，但**不能点任意歌**（Pick 返回 false，交给专属适配器）。
///
/// #10 事件驱动：只在首次 EnsureInitAsync 调一次 RequestAsync 并订阅
///   mgr.CurrentChanged / session.MediaPropertiesChanged / session.PlaybackInfoChanged，
///   把元数据/状态缓存下来；ReadNowPlaying() 仅读缓存并按墙钟外推进度（纯本地、无 WinRT 调用），
///   因此 MediaBus 的每拍轮询变廉价，不再每拍 RequestAsync。
///
/// 依赖 Windows.Media.Control（WinRT），需开发机带 Windows SDK 投影 pack 才能编译。
/// 与 FakeAdapter 实现同一 IPlayerAdapter，Host.Core 无需改动即可从假适配器切到真实 SMTC。
/// </summary>
public sealed class SmtcAdapter : IPlayerAdapter, IDisposable
{
    public string AppId => "smtc-generic";
    public AdapterCapability Caps => AdapterCapability.ReadMeta | AdapterCapability.Transport;

    private readonly object _lock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _init;

    // 缓存的元数据快照（事件刷新）
    private string _title = "", _artist = "", _album = "", _status = "stopped";
    private bool _hasThumb;
    private double _basePos;              // 事件时刻的播放位置（秒）
    private DateTime _baseUtc = DateTime.UtcNow;

    private Task? _initTask;

    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default)
    {
        // 首次触发异步初始化；之后同步读缓存
        if (!_init)
        {
            _initTask ??= EnsureInitAsync();
            if (!_initTask.Wait(2000, ct)) return Task.FromResult<NowPlaying?>(null); // 初始化超时兜底
        }
        lock (_lock)
        {
            if (_session is null) return Task.FromResult<NowPlaying?>(null);
            var pos = _status == "playing"
                ? _basePos + (DateTime.UtcNow - _baseUtc).TotalSeconds
                : _basePos;
            return Task.FromResult<NowPlaying?>(new NowPlaying
            {
                Title = _title, Artist = _artist,
                Album = string.IsNullOrEmpty(_album) ? null : _album,
                Pos = Math.Max(0, Math.Round(pos, 2)),
                Dur = 0, // SMTC 不直接给时长，由上层歌词/元数据补
                Status = _status,
                ArtRef = _hasThumb ? "smtc:thumb" : null,
                SourceMode = "PC_LOCAL"
            });
        }
    }

    private async Task EnsureInitAsync()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        mgr.CurrentChanged += OnCurrentChanged;
        lock (_lock) { _mgr = mgr; }
        HookSession(mgr.GetCurrentSession());
        _init = true;
    }

    private void OnCurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        try { HookSession(sender.GetCurrentSession()); } catch { /* 会话切换竞态，忽略本拍 */ }
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_lock)
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
            }
            _session = session;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
            }
        }
        RefreshCache();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => RefreshCache();

    private void RefreshCache()
    {
        var s = _session; // 读引用（原子）
        if (s is null) { lock (_lock) { _title = _artist = _album = ""; _status = "stopped"; _hasThumb = false; _basePos = 0; } return; }
        var mp = s.MediaProperties;
        var pi = s.PlaybackInfo;
        lock (_lock)
        {
            _title = mp.Title ?? "";
            _artist = mp.Artist ?? "";
            _album = mp.Album ?? "";
            _hasThumb = s.ThumbnailProperties is not null;
            _status = MapStatus(pi.PlaybackStatus);
            _basePos = pi.PlaybackPosition.TotalSeconds;
            _baseUtc = DateTime.UtcNow;
        }
    }

    private static string MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus st) => st switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
        _ => "stopped",
    };

    private GlobalSystemMediaTransportControlsSession? Session
    {
        get { if (!_init) { _initTask ??= EnsureInitAsync(); _initTask.Wait(2000); } return _session; }
    }

    private static async Task<bool> Run(GlobalSystemMediaTransportControlsSession? s,
        Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> op)
        => s is not null && await op(s).AsTask();

    public async Task<bool> Play(CancellationToken ct = default) => await Run(Session, x => x.TryPlayAsync());
    public async Task<bool> Pause(CancellationToken ct = default) => await Run(Session, x => x.TryPauseAsync());
    public async Task<bool> Next(CancellationToken ct = default) => await Run(Session, x => x.TrySkipNextAheadAsync());
    public async Task<bool> Prev(CancellationToken ct = default) => await Run(Session, x => x.TrySkipPreviousAsync());
    public async Task<bool> Stop(CancellationToken ct = default) => await Run(Session, x => x.TryStopAsync());

    // SMTC 无“播放指定歌曲”能力；点任意歌由 QQ/网易云等专属适配器实现（UIA/私有协议）。
    public Task<bool> Pick(string title, string artist, CancellationToken ct = default) => Task.FromResult(false);

    // SMTC 通用适配器无搜索能力（Caps 不含 Search）；返回空列表，由专属适配器实现。
    public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());

    public void Dispose()
    {
        lock (_lock)
        {
            if (_mgr is not null) _mgr.CurrentChanged -= OnCurrentChanged;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
            }
        }
    }
}
