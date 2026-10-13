using SongLink.Protocol;

namespace SongLink.Host;

[Flags]
public enum AdapterCapability
{
    None = 0,
    ReadMeta = 1 << 0,   // 能读当前曲目元数据（SMTC）
    Transport = 1 << 1,  // 能播/停/上下曲
    Pick = 1 << 2,       // 能点任意歌（专属适配器）
    Search = 1 << 3,     // 能返回搜索结果
    Volume = 1 << 4,     // 能调音量（SMTC/WASAPI/系统混音器等）
}

/// <summary>
/// 音乐 App 适配器接口（架构 §6.1）。SMTC/原生 App/本地音源各自实现；
/// Host.Core 只依赖此接口，把第三方不确定性隔离在实现里。
/// </summary>
public interface IPlayerAdapter
{
    string AppId { get; }
    AdapterCapability Caps { get; }

    /// <summary>读当前播放真值；无活动会话返回 null。</summary>
    Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default);

    Task<bool> Play(CancellationToken ct = default);
    Task<bool> Pause(CancellationToken ct = default);
    Task<bool> Next(CancellationToken ct = default);
    Task<bool> Prev(CancellationToken ct = default);
    Task<bool> Stop(CancellationToken ct = default);

    /// <summary>点任意歌（Caps 含 Pick 才支持）。</summary>
    Task<bool> Pick(string title, string artist, CancellationToken ct = default);

    /// <summary>搜索（Caps 含 Search 才支持）；不支持返回空列表。</summary>
    Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default);

    /// <summary>读当前音量 0.0~1.0（不支持 / 失败返回 1.0）。Caps 含 Volume 才调。</summary>
    Task<double> GetVolume(CancellationToken ct = default);

    /// <summary>设置音量 0.0~1.0（裁剪到 [0,1]）。Caps 含 Volume 才调；失败返回 false。</summary>
    Task<bool> SetVolume(double volume, CancellationToken ct = default);
}
