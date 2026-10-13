using Windows.Foundation;
using Windows.Media.Control;
using SongLink.Protocol;

namespace SongLink.Host.Adapters.Smtc;

/// <summary>
/// SMTC 通用适配器（架构 §6.1 梯队 1）：读系统"正在播放"会话的元数据/进度/封面，并下发传输控制。
/// 跨 App 稳定，但**不能点任意歌**（Pick 返回 false，交给专属适配器）。
///
/// #10 事件驱动：只在首次 EnsureInitAsync 调一次 RequestAsync 并订阅
///   mgr.CurrentSessionChanged / session.MediaPropertiesChanged / session.PlaybackInfoChanged，
///   把元数据/状态缓存下来；ReadNowPlaying() 仅读缓存并按墙钟外推进度（纯本地、无 WinRT 调用），
///   因此 MediaBus 的每拍轮询变廉价，不再每拍 RequestAsync。
///
/// 与 FakeAdapter 实现同一 IPlayerAdapter，Host.Core 无需改动即可从假适配器切到真实 SMTC。
///
/// 注：Windows.Media.Control 的真实 API 全部是异步取值——元数据走
/// TryGetMediaPropertiesAsync()，播放信息走 GetPlaybackInfo()，时间轴走 GetTimelineProperties()；
/// 事件是 CurrentSessionChanged（不是 CurrentChanged），切歌是 TrySkipNextAsync（不是 Ahead）。
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

    // 事件委托需要成对订阅/退订，故持有引用
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _onCurrentSessionChanged;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _onMediaPropsChanged;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _onPlaybackInfoChanged;

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
                Dur = _dur,
                Status = _status,
                ArtRef = _hasThumb ? "smtc:thumb" : null,
                SourceMode = "PC_LOCAL"
            });
        }
    }

    private double _dur;

    private async Task EnsureInitAsync()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _onCurrentSessionChanged = (sender, _) =>
        {
            try { HookSession(sender.GetCurrentSession()); } catch { /* 会话切换竞态，忽略本拍 */ }
        };
        mgr.CurrentSessionChanged += _onCurrentSessionChanged;
        lock (_lock) { _mgr = mgr; }
        HookSession(mgr.GetCurrentSession());
        _init = true;
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        var old = _session;
        if (old is not null)
        {
            if (_onMediaPropsChanged is not null) old.MediaPropertiesChanged -= _onMediaPropsChanged;
            if (_onPlaybackInfoChanged is not null) old.PlaybackInfoChanged -= _onPlaybackInfoChanged;
            _onMediaPropsChanged = null;
            _onPlaybackInfoChanged = null;
        }

        _session = session;
        if (session is not null)
        {
            _onMediaPropsChanged = (_, _) => _ = RefreshCacheAsync();
            _onPlaybackInfoChanged = (_, _) => _ = RefreshCacheAsync();
            session.MediaPropertiesChanged += _onMediaPropsChanged;
            session.PlaybackInfoChanged += _onPlaybackInfoChanged;
        }
        _ = RefreshCacheAsync();
    }

    /// <summary>从当前会话拉一次元数据/状态快照并写入缓存。事件线程上以 fire-and-forget 调用。</summary>
    private async Task RefreshCacheAsync()
    {
        var s = _session; // 读引用（原子）
        if (s is null)
        {
            lock (_lock) { _title = _artist = _album = ""; _status = "stopped"; _hasThumb = false; _basePos = 0; _dur = 0; }
            return;
        }

        string title = "", artist = "", album = "", status = "stopped";
        bool hasThumb = false;
        double pos = 0, dur = 0;

        try
        {
            var mp = await s.TryGetMediaPropertiesAsync().AsTask(); // 可能返回 null（读取失败）
            if (mp is not null)
            {
                title = mp.Title ?? "";
                artist = mp.Artist ?? "";
                album = mp.AlbumTitle ?? "";
                hasThumb = mp.Thumbnail is not null;
            }

            var pi = s.GetPlaybackInfo();
            status = MapStatus(pi?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped);

            var tp = s.GetTimelineProperties();
            if (tp is not null)
            {
                pos = tp.Position.TotalSeconds;
                dur = Math.Max(0, (tp.EndTime - tp.StartTime).TotalSeconds);
            }
        }
        catch
        {
            // 会话在读取过程中被关闭/切换：保留上一拍缓存，不清空
            return;
        }

        lock (_lock)
        {
            _title = title; _artist = artist; _album = album;
            _status = status; _hasThumb = hasThumb;
            _basePos = pos; _dur = dur;
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
    public async Task<bool> Next(CancellationToken ct = default) => await Run(Session, x => x.TrySkipNextAsync());
    public async Task<bool> Prev(CancellationToken ct = default) => await Run(Session, x => x.TrySkipPreviousAsync());
    public async Task<bool> Stop(CancellationToken ct = default) => await Run(Session, x => x.TryStopAsync());

    // SMTC 无“播放指定歌曲”能力；点任意歌由 QQ/网易云等专属适配器实现（UIA/私有协议）。
    public Task<bool> Pick(string title, string artist, CancellationToken ct = default) => Task.FromResult(false);

    // SMTC 通用适配器无搜索能力（Caps 不含 Search）；返回空列表，由专属适配器实现。
    public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());

    public void Dispose()
    {
        var mgr = _mgr;
        var session = _session;
        if (mgr is not null && _onCurrentSessionChanged is not null) mgr.CurrentSessionChanged -= _onCurrentSessionChanged;
        if (session is not null)
        {
            if (_onMediaPropsChanged is not null) session.MediaPropertiesChanged -= _onMediaPropsChanged;
            if (_onPlaybackInfoChanged is not null) session.PlaybackInfoChanged -= _onPlaybackInfoChanged;
        }
        _onCurrentSessionChanged = null;
        _onMediaPropsChanged = null;
        _onPlaybackInfoChanged = null;
        _mgr = null;
        _session = null;
        _init = false;
    }
}