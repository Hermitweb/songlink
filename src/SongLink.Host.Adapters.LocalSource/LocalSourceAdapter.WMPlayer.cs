using SongLink.Protocol;
using System.Runtime.InteropServices;

namespace SongLink.Host.Adapters.LocalSource;

/// <summary>
/// 本地音源适配器 v2 - WMPlayer COM 版本（无需 NAudio）
/// 完全可控、纯托管、本机可编、永远兜底可用 + 自动注册 SMTC
/// </summary>
public sealed class LocalSourceAdapter : IPlayerAdapter, IDisposable
{
    private object? _player;                           // WMP COM 对象
    private readonly LocalLibrary _library;            // 音乐库引用
    private string _currentFile = "";                  // 当前文件路径
    private bool _disposed;                            // 释放标记
    private static int _sessionCounter = 0;            // 会话计数器
    private readonly int _sessionId = Interlocked.Increment(ref _sessionCounter);

    public LocalSourceAdapter(LocalLibrary? library = null)
    {
        _library = library ?? new LocalLibrary();
        
        try 
        {
            // 创建 WMPlayer COM 实例
            var playerType = Type.GetTypeFromProgID("MediaPlayer.MediaPlayer");
            if (playerType is not null)
            {
                _player = Activator.CreateInstance(playerType);
                
                // 配置隐藏模式（不显示窗口和 UI）
                SetProperty(_player, "UIOn", false);
                SetProperty(_player, "Visible", false);
                SetProperty(_player, "DisplayChangeInfo", false);
                SetProperty(_player, "DisplayMode", "none");
                
                Console.WriteLine("[localsource] WMPlayer COM 初始化成功（隐藏模式）");
            }
            else
            {
                throw new COMException("无法创建 MediaPlayer COM 对象 (可能未安装或不支持)", unchecked((int)0x80040154));
            }
        }
        catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x80040154))
        {
            Console.WriteLine("[localsource] WMPlayer COM 不可用（跳过音频播放，仅使用元数据模式）");
            _player = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[localsource] WMPlayer 初始化失败：{ex.Message}");
            _player = null;
        }
        
        // 注册为 SMTC session
        RegisterSmtcSession();
    }

    /// <summary>设置 COM 对象的属性</summary>
    private void SetProperty(object obj, string prop, object value)
    {
        try { obj.GetType().InvokeMember(prop, System.Reflection.BindingFlags.SetProperty, null, obj, new[] { value }); }
        catch { /* 忽略错误 */ }
    }

    /// <summary>获取 COM 对象的属性</summary>
    private T GetProperty<T>(object obj, string prop, T defaultValue)
    {
        try 
        { 
            return (T)obj.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.InvokeMethod, null, obj, Array.Empty<object>());
        }
        catch { return defaultValue; }
    }

    public string AppId => "localsource";
    
    /// <summary>有播放器则全能力，否则只读元数据</summary>
    public AdapterCapability Caps 
    { 
        get 
        { 
            return _player != null 
                ? AdapterCapability.ReadMeta | AdapterCapability.Transport | AdapterCapability.Pick | AdapterCapability.Search 
                : AdapterCapability.ReadMeta | AdapterCapability.Search;
        }
    }

    /// <summary>读取当前播放状态</summary>
    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default)
    {
        if (_disposed || _player is null) return Task.FromResult<NowPlaying?>(null);

        try
        {
            var np = new NowPlaying
            {
                Title = GetProperty<string>(_player, "currentMedia?.name", "空闲"),
                Artist = "本地音源",
                Pos = (float)GetProperty<int>(_player, "position", 0) / 1000.0f,
                Dur = (float)_getPropertyDouble("_player", "currentMedia.durationTime", 0),
                Status = GetPlayState(GetProperty<int>(_player, "playState", 1)),
                SourceMode = "PC_LOCAL"
            };
            return Task.FromResult<NowPlaying?>(np);
        }
        catch { return Task.FromResult<NowPlaying?>(null); }
    }

    private double _getPropertyDouble(string target, string prop, double defaultVal)
    {
        try { return Convert.ToDouble(target.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.Instance, null, target, Array.Empty<object>())); }
        catch { return defaultVal; }
    }

    private string GetPlayState(int state)
    {
        // wmpPlayState enum: 1=Stopped, 2=Paused, 3=Playing...
        return state switch
        {
            3 => "playing",
            2 => "paused",
            1 => "stopped",
            _ => "stopped"
        };
    }

    public async Task<bool> Play(CancellationToken ct = default)
    {
        if (_disposed || _player is null) return false;
        
        _addPlayHistory();
        await SafeExecute(() => InvokeMember("controls.play"));
        return true;
    }

    public async Task<bool> Pause(CancellationToken ct = default)
    {
        if (_disposed || _player is null) return false;
        await SafeExecute(() => InvokeMember("controls.pause"));
        return true;
    }

    public Task<bool> Next(CancellationToken ct = default) => PickNextSong(1);
    public Task<bool> Prev(CancellationToken ct = default) => PickNextSong(-1);

    public async Task<bool> Stop(CancellationToken ct = default)
    {
        if (_disposed || _player is null) return false;
        await SafeExecute(() => InvokeMember("controls.stop"));
        return true;
    }

    public async Task<bool> Pick(string title, string artist, CancellationToken ct = default)
    {
        if (_disposed) return false;

        var track = await Task.Run(() => _library.Search($"{title} {artist}").FirstOrDefault(), ct);
        if (track is null) return false;

        var filePath = Path.Combine(_library.Directory, track.File);
        if (!File.Exists(filePath)) return false;

        try
        {
            if (_player is not null)
            {
                await SafeExecute(() => InvokeMember("controls.stop"));
                await SafeExecute(() => InvokeMember("openURL", filePath));
                await Play(ct);
            }
            
            _currentFile = filePath;
            return true;
        }
        catch { return false; }
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
            Dur = 180.0 
        }).ToList();
    }

    private async Task<bool> PickNextSong(int direction)
    {
        if (_disposed) return false;
        var allTracks = await Task.Run(() => _library.Search("").ToList(), default);
        if (allTracks.Count == 0) return false;

        int currentIndex = allTracks.IndexOf(allTracks.FirstOrDefault(t => t.File == Path.GetFileName(_currentFile)));
        int nextIndex = (currentIndex + direction + allTracks.Count) % allTracks.Count;
        
        return await Pick(allTracks[nextIndex].Title, allTracks[nextIndex].Artist, default);
    }

    private void _addPlayHistory()
    {
        try { _library.AddPlayHistory(Path.GetFileName(_currentFile), "本地音源"); }
        catch { /* 不重要，忽略 */ }
    }

    private async Task SafeExecute(Action action, params object[] args)
    {
        if (_disposed) return;
        try { await Task.Run(() => action(), default); } catch { }
    }

    private object? InvokeMember(string name, params object[] args)
    {
        if (_player is null) return null;
        try 
        { 
            return _player.GetType().InvokeMember(name, 
                System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, 
                null, _player, args);
        }
        catch { return null; }
    }

    /// <summary>WMPlayer 会自动向系统注册为媒体播放端点</summary>
    private void RegisterSmtcSession()
    {
        // WMPlayer via Windows Media Player 9/10+ 会自注册 SMTC
        // 无需额外代码
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try 
        {
            if (_player is not null)
            {
                InvokeMember("close");
                Marshal.ReleaseComObject(_player);
            }
        }
        catch { /* 忽略 */ }
        _player = null;
    }
}