using SongLink.Host;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>
/// PlayQueue 队列管理增强功能单元测试
/// </summary>
public class PlayQueueTests
{
    [Fact]
    public void AddItem_IncreasesCount()
    {
        using var queue = new PlayQueue();
        
        queue.Add("Test Song", "Test Artist", "user1");
        
        Assert.Equal(1, queue.Count);
    }
    
    [Fact]
    public void RemoveAt_MidIndex_AdjustsCurrentIndex()
    {
        using var queue = new PlayQueue();
        
        queue.Add("Song 1", "Artist 1", "user");
        queue.Add("Song 2", "Artist 2", "user");
        queue.Add("Song 3", "Artist 3", "user");
        
        queue.SeekTo(1); // CurrentIndex = 1
        queue.RemoveAt(0); // Remove index 0
        
        Assert.Equal(2, queue.Count);
        Assert.Equal(0, queue.CurrentIndex); // Should auto-adjust
    }
    
    [Fact]
    public void Shuffle_RearrangesItems()
    {
        using var queue = new PlayQueue();
        
        queue.Add("A", "X", "u");
        queue.Add("B", "Y", "u");
        queue.Add("C", "Z", "u");
        
        queue.Shuffle();

        Assert.True(queue.IsShuffling);
        Assert.Equal(3, queue.Count);
    }

    /// <summary>
    /// 回归：旧实现把 available(过滤后列表)的下标直接当作 _items 的下标赋给 CurrentIndex，
    /// 越界后 _items[CurrentIndex] 会抛 ArgumentOutOfRangeException。
    /// </summary>
    [Fact]
    public void GetNextSong_Shuffling_StaysInRangeAndNeverRepeatsCurrent()
    {
        using var queue = new PlayQueue();

        queue.Add("A", "X", "u");
        queue.Add("B", "Y", "u");
        queue.Add("C", "Z", "u");

        queue.SeekTo(1);
        queue.Shuffle();

        for (int i = 0; i < 500; i++)
        {
            var prev = queue.CurrentIndex;
            var next = queue.GetNextSong();

            Assert.NotNull(next);
            Assert.InRange(queue.CurrentIndex, 0, queue.Count - 1);
            Assert.NotEqual(prev, queue.CurrentIndex); // 随机模式不应重复当前这首
        }
    }

    [Fact]
    public void GetNextSong_Shuffling_SingleItem_ReturnsItself()
    {
        using var queue = new PlayQueue();

        queue.Add("A", "X", "u");
        queue.Shuffle();

        var next = queue.GetNextSong();

        Assert.NotNull(next);
        Assert.Equal("A", next.Title);
    }
    
    [Fact]
    public void GetNextSong_ReturnsSequentialWhenNotShuffling()
    {
        using var queue = new PlayQueue();
        
        queue.Add("1", "", "");
        queue.Add("2", "", "");
        queue.Add("3", "", "");
        
        queue.SeekTo(0); // Set current index
        var next = queue.GetNextSong();
        
        Assert.NotNull(next);
        // FIFO：SeekTo(0) 后取下一首 → 索引推进到 1，返回第 2 首
        Assert.Equal("2", next.Title);
        Assert.Equal(1, queue.CurrentIndex);
    }
    
    [Fact]
    public void QueueChangedEvent_FiresWithCorrectData()
    {
        using var queue = new PlayQueue();
        bool eventFired = false;
        
        queue.Changed += (args) => 
        {
            eventFired = true;
            Assert.Equal("added", args.EventName);
            Assert.NotNull(args.Item);
        };
        
        queue.Add("Test Song", "Artist", "user");
        
        Assert.True(eventFired);
    }
}