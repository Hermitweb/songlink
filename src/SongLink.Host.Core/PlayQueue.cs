namespace SongLink.Host;

/// <summary>
/// 点歌队列（契约 §3.6 queue.*）+ 落雪音乐风格增强：Shuffle + AutoPlayNext
/// 支持加入/删除/置顶/清空 + 随机打乱 + 自动下一首播放
/// </summary>
public sealed class PlayQueue : IDisposable
{
    public sealed record QueueItem(string Title, string Artist, string By, bool IsShuffled = false);

    private readonly List<QueueItem> _items = new();
    private readonly Random _random = new();
    private readonly object _lock = new();
    
    /// <summary>是否启用随机模式</summary>
    public bool IsShuffling { get; private set; }
    
    /// <summary>当前正在播放的索引（-1 表示无）</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>队列内容变化时触发（供 HostServer 广播 queue-updated）。</summary>
    public event Action<QueueChangedEventArgs>? Changed;

    /// <summary>队列事件参数</summary>
    public sealed class QueueChangedEventArgs
    {
        public string EventName { get; set; } = ""; // "added", "removed", "topped", "cleared", "shuffled"
        public int? Index { get; set; }
        public QueueItem? Item { get; set; }
    }

    public void Add(string title, string artist, string by)
    {
        lock (_lock)
        {
            var item = new QueueItem(title, artist, by);
            _items.Add(item);
        }
        
        Changed?.Invoke(new QueueChangedEventArgs 
        { 
            EventName = "added", 
            Index = _items.Count - 1, 
            Item = _items.Last() 
        });
    }

    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _items.Count) return false;
            var removed = _items[index];
            _items.RemoveAt(index);
            
            if (index <= CurrentIndex && CurrentIndex > 0) CurrentIndex--;
            
            Changed?.Invoke(new QueueChangedEventArgs 
            { 
                EventName = "removed", 
                Index = index, 
                Item = removed 
            });
            
            return true;
        }
    }

    /// <summary>将指定索引的歌曲置顶（移至队首）</summary>
    public bool Top(int index)
    {
        lock (_lock)
        {
            if (index <= 0 || index >= _items.Count) return false;
            var item = _items[index];
            _items.RemoveAt(index);
            _items.Insert(0, item);
            
            if (CurrentIndex == index - 1) CurrentIndex = 0;
            
            Changed?.Invoke(new QueueChangedEventArgs 
            { 
                EventName = "topped", 
                Index = 0, 
                Item = item 
            });
            
            return true;
        }
    }

    /// <summary>随机打乱队列顺序</summary>
    public void Shuffle()
    {
        lock (_lock)
        {
            if (_items.Count < 2) return;
            
            for (int i = _items.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                var temp = _items[i];
                _items[j] = _items[i];
                _items[i] = temp;
            }
            
            IsShuffling = true;
            
            Changed?.Invoke(new QueueChangedEventArgs 
            { 
                EventName = "shuffled", 
                Index = null, 
                Item = null 
            });
        }
    }

    /// <summary>关闭随机模式并恢复原序</summary>
    public void Unshuffle()
    {
        lock (_lock)
        {
            IsShuffling = false;
            Changed?.Invoke(new QueueChangedEventArgs { EventName = "unshuffled" });
        }
    }

    /// <summary>清空队列</summary>
    public void Clear()
    {
        lock (_lock)
        {
            CurrentIndex = -1;
            _items.Clear();
            
            Changed?.Invoke(new QueueChangedEventArgs { EventName = "cleared" });
        }
    }

    /// <summary>获取快照列表</summary>
    public IReadOnlyList<QueueItem> Snapshot()
    {
        lock (_lock) return _items.ToList();
    }

    /// <summary>获取当前播放歌曲（如果有的话）</summary>
    public QueueItem? CurrentSong
    {
        get
        {
            lock (_lock)
            {
                return CurrentIndex >= 0 && CurrentIndex < _items.Count 
                    ? _items[CurrentIndex] 
                    : null;
            }
        }
    }

    /// <summary>移动到指定位置播放</summary>
    public void SeekTo(int index)
    {
        lock (_lock)
        {
            if (index >= 0 && index < _items.Count)
                CurrentIndex = index;
        }
    }

    /// <summary>
    /// 把 fromIndex 位置的项目移到 toIndex(toIndex 会夹到合法区间)。
    /// 保留 CurrentIndex — 当前在播的歌如果被移动,跟随到新位置。
    /// </summary>
    public bool Move(int fromIndex, int toIndex)
    {
        lock (_lock)
        {
            if (fromIndex < 0 || fromIndex >= _items.Count) return false;
            if (_items.Count <= 1) return true;
            if (toIndex < 0) toIndex = 0;
            if (toIndex >= _items.Count) toIndex = _items.Count - 1;
            if (fromIndex == toIndex) return true;

            var item = _items[fromIndex];
            _items.RemoveAt(fromIndex);
            _items.Insert(toIndex, item);

            // CurrentIndex 跟随:如果当前在播的是被移动的那条,跟随到新位置
            if (CurrentIndex == fromIndex) CurrentIndex = toIndex;
            else if (CurrentIndex > fromIndex && CurrentIndex <= toIndex) CurrentIndex--;
            else if (CurrentIndex < fromIndex && CurrentIndex >= toIndex) CurrentIndex++;

            Changed?.Invoke(new QueueChangedEventArgs { EventName = "moved", Index = toIndex, Item = item });
            return true;
        }
    }

    /// <summary>自动播放下一首（用于队列播放完毕或手动切歌）</summary>
    public QueueItem? GetNextSong()
    {
        lock (_lock)
        {
            if (_items.Count == 0) return null;
            
            if (IsShuffling)
            {
                // 随机取一首不同于当前播放的歌
                if (_items.Count == 1) return _items[0]; // 只有一首，无可选

                if (CurrentIndex < 0 || CurrentIndex >= _items.Count)
                {
                    // 当前索引无效（尚未开始播放或已被移除），直接随机一首
                    CurrentIndex = _random.Next(_items.Count);
                }
                else
                {
                    // 在 0.._items.Count-2 中取，再跳过 CurrentIndex 映射回真实下标
                    var nextIndex = _random.Next(_items.Count - 1);
                    if (nextIndex >= CurrentIndex) nextIndex++;
                    CurrentIndex = nextIndex;
                }
            }
            else
            {
                // FIFO：移到下一个
                CurrentIndex = (CurrentIndex + 1) % _items.Count;
            }
            
            return _items[CurrentIndex];
        }
    }

    public int Count => _items.Count;
    
    public void Dispose() { }
}
