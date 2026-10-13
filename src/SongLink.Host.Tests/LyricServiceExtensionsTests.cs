using System.IO;
using SongLink.Host;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// LyricServiceExtensions.ConvertLrclibToLrc 单元测试。
///
/// 旧实现对 LRCLib 返回的 syncedLyrics 字符串按空格切分再 double.Parse 第 0 段，
/// 但第 0 段是 "[01:23.45]" 这种带方括号带冒号的字符串，double.Parse 必抛 FormatException。
/// 实际 LRCLib 响应里的 syncedLyrics 已经是 LRC 格式（每行 [mm:ss.xx]text），
/// 应该直接透传，只在缺失时补 [ti:][ar:] 元数据头。
/// </summary>
public class LyricServiceExtensionsTests
{
    private static dynamic Lrc(string? synced, string? title, string? artist)
    {
        // LyricServiceExtensions.LrclibLyrics / TrackInfo 是 private nested,
        // 走 dynamic 反射构造即可，不污染生产代码的可见性。
        var asm = typeof(LyricService).Assembly;
        var lrc = asm.GetType("SongLink.Host.LyricServiceExtensions+LrclibLyrics")!;
        var ti  = asm.GetType("SongLink.Host.LyricServiceExtensions+TrackInfo")!;
        var inst = Activator.CreateInstance(lrc)!;
        if (synedSet(inst, "syncedLyrics", synced)) { }
        if (title is not null || artist is not null)
        {
            var track = Activator.CreateInstance(ti)!;
            synedSet(track, "title", title);
            synedSet(track, "artist", artist);
            lrc.GetProperty("track")!.SetValue(inst, track);
        }
        return inst;

        static bool synedSet(object o, string name, object? v)
        {
            var p = o.GetType().GetProperty(name);
            if (p is null || !p.CanWrite) return false;
            p.SetValue(o, v);
            return true;
        }
    }

    [Fact]
    public void ConvertLrclibToLrc_PreservesExistingLyricsAsIs()
    {
        var body =
            "[00:00.00]故事的小黄花\n" +
            "[00:03.50]从出生那年就飘着\n" +
            "[01:05.25]童年的荡秋千\n";

        var lrc = (string)typeof(LyricServiceExtensions)
            .GetMethod("ConvertLrclibToLrc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { Lrc(body, null, null) })!;

        // 两端都 TrimEnd 末尾空行，避免因 LRCLib 源是否带 \n 而产生差异
        Assert.Equal(body.TrimEnd('\n').TrimEnd('\r'), lrc.TrimEnd('\n').TrimEnd('\r'));
    }

    [Fact]
    public void ConvertLrclibToLrc_PrependsMissingMetadataHeaders()
    {
        var body = "[00:00.00]x\n[00:01.00]y\n";

        var lrc = (string)typeof(LyricServiceExtensions)
            .GetMethod("ConvertLrclibToLrc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { Lrc(body, "晴天", "周杰伦") })!;

        // [ti:] / [ar:] 头在歌词行之前
        Assert.StartsWith("[ti:晴天]\n[ar:周杰伦]\n[00:00.00]x", lrc);
    }

    [Fact]
    public void ConvertLrclibToLrc_DoesNotDuplicateExistingMetadata()
    {
        var body = "[ti:Original Title]\n[ar:Original Artist]\n[00:00.00]x\n";

        var lrc = (string)typeof(LyricServiceExtensions)
            .GetMethod("ConvertLrclibToLrc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { Lrc(body, "晴天", "周杰伦") })!;

        // 既有的 [ti:]/[ar:] 不应被覆盖
        Assert.DoesNotContain("晴天", lrc);
        Assert.Contains("[ti:Original Title]", lrc);
        Assert.Contains("[ar:Original Artist]", lrc);
    }

    [Fact]
    public void ConvertLrclibToLrc_EmptyBody_ReturnsEmpty()
    {
        var lrc = (string)typeof(LyricServiceExtensions)
            .GetMethod("ConvertLrclibToLrc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { Lrc(null, null, null) })!;

        Assert.Equal(string.Empty, lrc);
    }

    [Fact]
    public void LyricService_Parse_RoundtripsConvertLrclibToLrc_Output()
    {
        // 端到端：模拟 LRCLib 响应 → ConvertLrclibToLrc → LyricService.Parse → LyricLine 序列
        var body = "[00:00.00]第一行\n[00:03.50]第二行\n";
        var lrcText = (string)typeof(LyricServiceExtensions)
            .GetMethod("ConvertLrclibToLrc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { Lrc(body, "T", "A") })!;

        var lines = LyricService.Parse(lrcText);

        Assert.Equal(2, lines.Count);
        Assert.Equal(0.00, lines[0].T, 2);
        Assert.Equal("第一行", lines[0].X);
        Assert.Equal(3.50, lines[1].T, 2);
        Assert.Equal("第二行", lines[1].X);
    }
}