using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 状态广播去抖/节流（修复评审 #3）。Media Bus 每拍都广播会让客户端每秒收 4 个 state，
/// 与"省流量、pos 由客户端插值"的初衷矛盾。
/// 规则：签名（title|artist|status|sourceMode）变化 → 立即发；否则最多每 keepalive 发一次（pos 心跳刷新）。
/// 纯逻辑、可单测（传入确定时间）。
/// </summary>
public sealed class StateGate
{
    private readonly TimeSpan _keepalive;
    private string _lastSig = "";
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;

    public StateGate(TimeSpan? keepalive = null)
        => _keepalive = keepalive ?? TimeSpan.FromSeconds(1);

    private static string Sig(NowPlaying np) => $"{np.Title}|{np.Artist}|{np.Status}|{np.SourceMode}";

    /// <summary>判断此刻是否应广播该 np；会更新内部状态。</summary>
    public bool ShouldEmit(NowPlaying np, DateTimeOffset now)
    {
        var sig = Sig(np);
        if (sig != _lastSig)
        {
            _lastSig = sig; _lastEmit = now; return true;   // 换曲/状态变：立即
        }
        if (now - _lastEmit >= _keepalive)
        {
            _lastEmit = now; return true;                     // 未变：节流心跳
        }
        return false;
    }
}
