using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 广播重放缓冲（契约 §7）：保留最近 N 条"可重放"广播帧（state/lyric/event），按全局 seq 有序。
/// 客户端重连带 lastSeq，取 seq &gt; lastSeq 的补发。有界防内存涨。纯逻辑、可单测。
/// </summary>
public sealed class ReplayBuffer
{
    private readonly int _capacity;
    private readonly LinkedList<Frame> _items = new();

    public ReplayBuffer(int capacity = 128) => _capacity = Math.Max(1, capacity);

    /// <summary>追加一帧（其 Seq 已由调用方在锁内赋值）。</summary>
    public void Add(Frame frame)
    {
        _items.AddLast(frame);
        while (_items.Count > _capacity) _items.RemoveFirst();
    }

    /// <summary>返回 seq 严格大于 lastSeq 的帧（按 seq 升序）。</summary>
    public List<Frame> Since(long lastSeq)
    {
        var list = new List<Frame>();
        foreach (var f in _items) if (f.Seq > lastSeq) list.Add(f);
        return list;
    }

    public int Count => _items.Count;
    public void Clear() => _items.Clear();
}
