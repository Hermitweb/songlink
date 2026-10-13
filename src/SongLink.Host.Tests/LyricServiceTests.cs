using System.IO;
using SongLink.Host;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// LyricService 单元测试 — Parse / TryGet / SetOffset/GetOffset / Key。
/// 验证本地歌词加载契约,不依赖联网或音频设备。
/// </summary>
public class LyricServiceTests : IDisposable
{
    private readonly string _tmp;

    public LyricServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "songlink-lyric-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Parse_handles_mm_ss_xx_format()
    {
        var lrc =
            "[00:00.00]故事的小黄花\n" +
            "[00:03.50]从出生那年就飘着\n" +
            "[01:05.25]童年的荡秋千\n";
        var lines = LyricService.Parse(lrc);
        Assert.Equal(3, lines.Count);
        Assert.Equal(0.0, lines[0].T);
        Assert.Equal(3.5, lines[1].T);
        Assert.Equal(65.25, lines[2].T);
        Assert.Equal("故事的小黄花", lines[0].X);
    }

    [Fact]
    public void Parse_supports_multi_timestamp_on_one_line()
    {
        // LRC 惯例一行可多个时间标签,如 [00:01.00][00:30.00]重复句
        var lrc = "[00:01.00][00:30.00]副歌";
        var lines = LyricService.Parse(lrc);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1.0, lines[0].T);
        Assert.Equal(30.0, lines[1].T);
        Assert.Equal("副歌", lines[0].X);
        Assert.Equal("副歌", lines[1].X);
    }

    [Fact]
    public void Parse_ignores_meta_tags_only_lines()
    {
        var lrc =
            "[ti:晴天]\n" +
            "[ar:周杰伦]\n" +
            "[00:00.00]actual lyric\n";
        var lines = LyricService.Parse(lrc);
        Assert.Single(lines);
        Assert.Equal("actual lyric", lines[0].X);
    }

    [Fact]
    public void Parse_supports_colon_dot_frac_form()
    {
        // [00:01:23] = 0分1秒23分秒 = 1.23 秒 (regex 的 [.:] 同时吃 . 和 :)
        var lrc = "[00:01:23]one line\n";
        var lines = LyricService.Parse(lrc);
        Assert.Single(lines);
        Assert.Equal(1.23, lines[0].T);
    }

    [Fact]
    public void SetOffset_then_GetOffset_returns_per_track_value()
    {
        var svc = new LyricService(_tmp);
        svc.SetOffset("Yellow", "Coldplay", 300);
        svc.SetOffset("晴天", "周杰伦", -120);
        Assert.Equal(300, svc.GetOffset("Yellow", "Coldplay"));
        Assert.Equal(-120, svc.GetOffset("晴天", "周杰伦"));
        // 未设的曲应回退到全局默认 0
        Assert.Equal(0, svc.GetOffset("Unknown", "X"));
    }

    [Fact]
    public void SetOffset_with_empty_title_sets_global_default()
    {
        var svc = new LyricService(_tmp);
        svc.SetOffset(null, null, 50);
        // 全曲应回退到 50
        Assert.Equal(50, svc.GetOffset("AnyTitle", "AnyArtist"));
    }

    [Fact]
    public void TryGet_loads_lrc_file_and_attaches_offset()
    {
        // 写一个标准文件
        var key = "Yellow - Coldplay";
        File.WriteAllText(Path.Combine(_tmp, key + ".lrc"),
            "[00:00.00]look at the stars\n[00:05.00]look how they shine for you\n");
        var svc = new LyricService(_tmp);
        svc.SetOffset("Yellow", "Coldplay", 250);
        var lyric = svc.TryGet("Yellow", "Coldplay");
        Assert.NotNull(lyric);
        Assert.Equal(2, lyric!.Lines.Count);
        Assert.Equal(250, lyric.OffsetMs);
        Assert.Equal("lrc:Yellow-Coldplay", lyric.TrackId);
    }

    [Fact]
    public void TryGet_returns_null_when_file_missing()
    {
        var svc = new LyricService(_tmp);
        Assert.Null(svc.TryGet("不存在", "Nobody"));
    }

    [Fact]
    public void TryGet_returns_null_when_title_empty()
    {
        var svc = new LyricService(_tmp);
        Assert.Null(svc.TryGet("", "Nobody"));
        Assert.Null(svc.TryGet("   ", "Nobody"));
    }

    [Fact]
    public void TryGet_returns_null_when_lrc_has_no_timed_lines()
    {
        File.WriteAllText(Path.Combine(_tmp, "Empty - X.lrc"),
            "[ti:仅元数据]\n[ar:X]\n");
        var svc = new LyricService(_tmp);
        Assert.Null(svc.TryGet("Empty", "X"));
    }

    [Fact]
    public void Key_strips_invalid_chars_so_write_and_read_match()
    {
        // 文件名带 / 或 ? 在 Windows 是非法的,Key 必须过滤;
        // 否则写入用某种路径生成,TryGet 又用 Key 计算 → 永远读不到。
        var svc = new LyricService(_tmp);
        File.WriteAllText(Path.Combine(_tmp, "ACDC - Highway to Hell.lrc"),
            "[00:01.00]easy come easy go\n");
        // Title 含 / — 不应抛
        var lyric = svc.TryGet("AC/DC", "Highway to Hell");
        Assert.NotNull(lyric);
        Assert.Single(lyric!.Lines);
    }
}
