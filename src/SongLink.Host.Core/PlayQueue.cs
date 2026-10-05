namespace SongLink.Host;

/// <summary>
/// 点歌队列（契约 §3.6 queue.*）：多人点歌的 FIFO 队列，支持加入/删除/置顶/清空。
/// 与"当前播放"解耦——队列只管排队，实际顺序播放由适配层驱动（M2）。变更触发 <see cref="Changed"/>。
/// 纯托管、可本机测。
/// </summary>
public sealed class PlayQueue
{
    public sealed record QueueItem(string Title, string Artist, string By);

    private readonly List<QueueItem> _items = new();
    private readonly object _lock = new();

    /// <summary>队列内容变化时触发（供 HostServer 广播 queue-updated）。</summary>
    public event Action? Changed;

    public void Add(string title, string artist, string by)
    {
        lock (_lock) _items.Add(new QueueItem(title, artist, by));
        Changed?.Invoke();
    }

    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _items.Count) return false;
            _items.RemoveAt(index);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>把 index 处的歌置顶（移到队首）。</summary>
    public bool Top(int index)
    {
        lock (_lock)
        {
            if (index <= 0 || index >= _items.Count) return false;
            var it = _items[index];
            _items.RemoveAt(index);
            _items.Insert(0, it);
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
        Changed?.Invoke();
    }

    public List<QueueItem> Snapshot() { lock (_lock) return _items.ToList(); }
    public int Count { get { lock (_lock) return _items.Count; } }
}
