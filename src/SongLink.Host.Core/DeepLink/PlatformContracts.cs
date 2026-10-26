namespace SongLink.Host.DeepLink;

/// <summary>
/// 一个在线平台当前**真实**能做到什么。
///
/// <para>为什么要有这个分级：三家客户端的"点歌"通道差别极大 ——
/// 网易云桌面端有实测可用的 CDP 控制通道，QQ音乐/酷狗的桌面端没有可用的注入通道，
/// 只能靠界面自动化点搜索框。面板上如果一律显示"可点歌"，用户点下去发现放不出来，
/// 那比一开始就说清楚糟糕得多。</para>
/// </summary>
public enum PlatformCapability
{
    /// <summary>完全不可用（接口挂了/未启用）。</summary>
    None = 0,

    /// <summary>只能搜到歌，点不动。</summary>
    SearchOnly = 1,

    /// <summary>能搜到歌，也能真的让它响起来（点完会校验"确实在放"）。</summary>
    SearchAndPlay = 2,

    /// <summary>只能点（客户端界面自动化），没有可用的在线搜索源。</summary>
    PlayOnly = 3,
}

/// <summary>一次播放尝试的结果。失败时必须带出**具体**原因，不允许"失败了"三个字了事。</summary>
public enum PickStatus
{
    /// <summary>成功：客户端确实在放这首歌。</summary>
    Started = 0,

    /// <summary>平台不支持点歌（没有可用的客户端控制通道）。</summary>
    Unsupported = 1,

    /// <summary>需要会员/付费才能听。</summary>
    NeedsMembership = 2,

    /// <summary>客户端在放，但放的不是点的那首（点错位置/结果顺序不符）。</summary>
    WrongSong = 3,

    /// <summary>客户端没启动，或没响应。</summary>
    NotRunning = 4,

    /// <summary>其它失败。</summary>
    Failed = 5,
}

/// <summary>点歌结果。</summary>
/// <param name="Status">状态。</param>
/// <param name="Detail">给用户看的原因（中文，直接可显示）。</param>
public sealed record PickOutcome(PickStatus Status, string? Detail = null)
{
    public bool Ok => Status == PickStatus.Started;

    public static PickOutcome Started(string? detail = null) => new(PickStatus.Started, detail);
}

/// <summary>系统媒体会话（SMTC）读到的一帧播放快照。</summary>
/// <param name="Title">曲目名。</param>
/// <param name="Artist">歌手。</param>
/// <param name="Status">播放状态（playing/paused/stopped 等）。</param>
/// <param name="AppId">来源 App 的标识（如 QQMusic.exe / kugou）。</param>
public sealed record PlaybackSnapshot(string Title, string Artist, string Status, string AppId = "")
{
    public bool IsPlaying =>
        string.Equals(Status, "playing", StringComparison.OrdinalIgnoreCase);
}

/// <summary>某个在线平台的状态（给界面用）。</summary>
/// <param name="Kind">平台标识（netease/qq/kugou…）。</param>
/// <param name="Name">展示名。</param>
/// <param name="Capability">真实能力。</param>
/// <param name="Note">补充说明/最近一次失败原因。</param>
public sealed record PlatformStatus(string Kind, string Name, PlatformCapability Capability, string? Note = null);

/// <summary>
/// 适配器的诊断信息（点歌失败原因、平台能力）。
///
/// <para>由 <c>TakeoverAdapter</c> 实现；<c>HostServer</c> 用它把"为什么点歌失败"
/// 透到面板上，而不是丢一句 APP_NOT_FOUND 让用户自己猜。</para>
/// </summary>
public interface IAdapterDiagnostics
{
    /// <summary>最近一次点歌失败的原因（中文，可直接显示）。成功时为 null。</summary>
    string? LastPickFailureReason { get; }

    /// <summary>各在线平台当前能力。</summary>
    IReadOnlyList<PlatformStatus> PlatformStatuses { get; }
}

/// <summary>
/// 读"电脑上现在真的在放什么"的探针。
///
/// <para>用途：**点歌成功不能只等于"命令发出去了"**。网易云的实测教训是
/// 切歌成功但状态是 Stop（没版权），如果那样也算成功，用户看到"点歌成功"却没声音。
/// 所以任何"点歌"动作后面都要用探针确认：客户端确实切到了目标歌，且状态是在播。</para>
/// </summary>
public interface IPlaybackProbe
{
    /// <summary>读一帧当前播放快照；没有活动会话时返回 null。</summary>
    Task<PlaybackSnapshot?> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// 传输控制（播放/暂停），通过系统媒体会话下发。
///
/// <para>为什么需要它：实测 QQ音乐 界面点歌后**切歌成功但播放器是暂停的**
/// （客户端原本就是暂停状态，新歌继承了这个状态）。这时"歌是对的"但没声音，
/// 必须补一个"播放"指令才算点歌成功。</para>
/// </summary>
public interface IPlaybackControl
{
    /// <summary>让当前会话继续播放。返回 false 表示没有可控制的会话或调用失败。</summary>
    Task<bool> PlayAsync(CancellationToken ct = default);

    /// <summary>暂停当前会话。</summary>
    Task<bool> PauseAsync(CancellationToken ct = default);
}

/// <summary>
/// "在某个音乐客户端里点一首歌"的执行器（通常是界面自动化）。
///
/// <para>为什么需要它：QQ音乐/酷狗的 Windows 客户端没有可用的注入通道
/// （实测：QQ音乐无 CDP、命令行参数无效；酷狗远程调试被编译版禁用），
/// 唯一能用的通用办法是驱动它自己的搜索框：搜歌名 → 回车 → 点第一条结果。</para>
/// </summary>
public interface IMusicPicker
{
    /// <summary>目标客户端标识（用于日志与错误提示）。</summary>
    string AppId { get; }

    /// <summary>
    /// 在客户端里点歌。返回结果必须说明成功与否、失败原因。
    /// </summary>
    /// <param name="title">歌名。</param>
    /// <param name="artist">歌手。</param>
    /// <param name="probe">
    /// 播放探针（可选）。给了的话，点歌器可以**逐行试点并用探针确认**：
    /// 坐标点歌只能"点某一行的某个位置"，而客户端的列表布局会随版本/窗口大小变，
    /// 试几行、用系统媒体会话确认哪一行才是目标歌，比赌一个固定坐标可靠得多。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    Task<PickOutcome> PickAsync(string title, string artist, IPlaybackProbe? probe = null, CancellationToken ct = default);
}
