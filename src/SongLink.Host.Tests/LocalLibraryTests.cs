using System.IO;
using SongLink.Host;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// LocalLibrary 边界测试：导入/搜索/批量/历史/Tracks 属性。
/// 不依赖网络/音频设备,纯本地文件 + 字符串解析。
/// </summary>
public class LocalLibraryTests : IDisposable
{
    private readonly string _tmp;

    public LocalLibraryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "songlink-lib-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact]
    public void Import_dedups_by_title_artist()
    {
        var lib = new LocalLibrary(_tmp);
        var t1 = lib.Import("Yellow", "Coldplay", new byte[] { 1, 2, 3 });
        var t2 = lib.Import("Yellow", "Coldplay", new byte[] { 4, 5, 6 });
        Assert.Same(t1, t2);   // 同一首不应重复
        Assert.Equal(1, lib.Count);
    }

    [Fact]
    public void Import_strips_invalid_filename_chars_via_PathSafeName()
    {
        var lib = new LocalLibrary(_tmp);
        var t = lib.Import("AC/DC", "High?way", null);
        // PathSafeName 是过滤(去掉非法字符),不是替换 — "/" 和 "?" 直接消失
        Assert.DoesNotContain("/", t.File);
        Assert.DoesNotContain("?", t.File);
        Assert.Equal("ACDC - Highway.mp3", t.File);
    }

    [Fact]
    public void Search_returns_tracks_matching_title_or_artist()
    {
        var lib = new LocalLibrary(_tmp);
        lib.Import("Yellow", "Coldplay", null);
        lib.Import("晴天", "周杰伦", null);
        lib.Import("稻香", "周杰伦", null);
        Assert.Equal(2, lib.Search("周杰伦").Count);
        Assert.Single(lib.Search("Yellow"));
        Assert.Single(lib.Search("晴天"));
    }

    [Fact]
    public void Search_empty_keyword_returns_empty()
    {
        var lib = new LocalLibrary(_tmp);
        lib.Import("X", "Y", null);
        Assert.Empty(lib.Search(""));
        Assert.Empty(lib.Search("   "));
    }

    [Fact]
    public void Tracks_property_returns_snapshot_safe_to_iterate()
    {
        var lib = new LocalLibrary(_tmp);
        lib.Import("A", "X", null);
        lib.Import("B", "Y", null);
        var snap = lib.Tracks;
        Assert.Equal(2, snap.Count);
        // 后续插入不应影响快照(因为 Tracks 返回的是 List 拷贝)
        lib.Import("C", "Z", null);
        Assert.Equal(2, snap.Count);
        Assert.Equal(3, lib.Tracks.Count);
    }

    [Fact]
    public void AddPlayHistory_caps_at_50_entries()
    {
        var lib = new LocalLibrary(_tmp);
        for (int i = 0; i < 60; i++)
            lib.AddPlayHistory($"T{i}", "Artist");
        // 历史最多 50 条
        var hist = lib.GetPlayHistory(100);
        Assert.Equal(50, hist.Count);
        // 最新加的在最前(T59)
        Assert.Equal("T59", hist[0].Title);
    }

    [Fact]
    public void AddPlayHistory_persists_to_disk_and_reloads()
    {
        var lib1 = new LocalLibrary(_tmp);
        lib1.AddPlayHistory("X", "Y");
        // 构造新实例 — 应从 history.json 重载
        var lib2 = new LocalLibrary(_tmp);
        var hist = lib2.GetPlayHistory(10);
        Assert.Single(hist);
        Assert.Equal("X", hist[0].Title);
    }

    [Fact]
    public async Task ImportBatchAsync_processes_all_files()
    {
        // 写 3 个临时音频文件供批量导入用
        var batchDir = Path.Combine(_tmp, "batch");
        Directory.CreateDirectory(batchDir);
        File.WriteAllBytes(Path.Combine(batchDir, "晴天 - 周杰伦.mp3"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(batchDir, "Yellow - Coldplay.mp3"), new byte[] { 4, 5, 6 });
        File.WriteAllBytes(Path.Combine(batchDir, "no_dash.mp3"), new byte[] { 7, 8, 9 });

        var lib = new LocalLibrary(_tmp);
        var result = await lib.ImportBatchAsync(Directory.GetFiles(batchDir));
        Assert.Equal(3, result.TotalFiles);
        Assert.Equal(3, result.SuccessfulImportsList.Count);
        Assert.Equal(3, lib.Count);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(3, lib.Count);
    }

    [Fact]
    public async Task ImportBatchAsync_survives_corrupt_files()
    {
        var batchDir = Path.Combine(_tmp, "batch2");
        Directory.CreateDirectory(batchDir);
        File.WriteAllBytes(Path.Combine(batchDir, "晴天 - 周杰伦.mp3"), new byte[] { 1 });
        // 制造一个不存在文件路径
        var lib = new LocalLibrary(_tmp);
        var paths = new[] { Path.Combine(batchDir, "晴天 - 周杰伦.mp3"), Path.Combine(batchDir, "ghost.mp3") };
        var result = await lib.ImportBatchAsync(paths);
        Assert.Equal(2, result.TotalFiles);
        Assert.Single(result.SuccessfulImportsList);
        Assert.Equal(1, result.FailedFiles);
    }

    [Fact]
    public void Reload_picks_up_index_persisted_by_previous_instance()
    {
        var lib1 = new LocalLibrary(_tmp);
        lib1.Import("持久化测试", "周杰伦", new byte[] { 0xAB });
        // 新实例 — 应从 index.json 重载
        var lib2 = new LocalLibrary(_tmp);
        Assert.Equal(1, lib2.Count);
        Assert.Equal("持久化测试", lib2.Tracks[0].Title);
    }
}
