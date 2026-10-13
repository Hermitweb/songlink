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
        Assert.Equal("1", next.Title);
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