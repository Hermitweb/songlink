using SongLink.Host.Adapters.UiaPick;
using SongLink.Host.DeepLink;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// 「客户端界面点歌」（坐标驱动）与「真在播校验」的单测。
///
/// <para>这两块是 QQ音乐/酷狗 能点歌的关键，也是最容易出错的地方：
/// 坐标算错就点歪（点到了"播放全部"），校验放松就会"点歌成功却没声音"。</para>
/// </summary>
public class CoordinatePickTests
{
    // ------------------------------------------------------------ 播放校验

    [Fact]
    public void Verifier_accepts_the_exact_song()
    {
        var snap = new PlaybackSnapshot("晴天", "周杰伦", "playing", "QQMusic.exe");
        Assert.True(PlaybackVerifier.Matches(snap, "晴天", "周杰伦"));
    }

    [Fact]
    public void Verifier_accepts_a_song_title_with_suffix()
    {
        // 客户端标题常带后缀
        var snap = new PlaybackSnapshot("晴天 (Live)", "周杰伦", "playing", "QQMusic.exe");
        Assert.True(PlaybackVerifier.Matches(snap, "晴天", "周杰伦"));
    }

    [Fact]
    public void Verifier_accepts_a_concatenated_playlist_title()
    {
        // QQ音乐 连播时会把整串歌名拼成一个标题
        var snap = new PlaybackSnapshot("晴天+安静+修炼爱情", "周杰伦 / 林俊杰", "playing", "QQMusic.exe");
        Assert.True(PlaybackVerifier.Matches(snap, "晴天", "周杰伦"));
    }

    [Fact]
    public void Verifier_rejects_a_different_song()
    {
        var snap = new PlaybackSnapshot("稻香", "周杰伦", "playing", "QQMusic.exe");
        Assert.False(PlaybackVerifier.Matches(snap, "晴天", "周杰伦"));
    }

    [Fact]
    public void Verifier_rejects_a_cover_with_another_singer()
    {
        var snap = new PlaybackSnapshot("晴天", "某某翻唱", "playing", "QQMusic.exe");
        Assert.False(PlaybackVerifier.Matches(snap, "晴天", "周杰伦"));
    }

    [Fact]
    public void Verifier_does_not_match_a_single_character_title()
    {
        // 单字歌名做"包含"匹配太容易误判（"光" 能命中一大堆），
        // 但**完全相同**的单字标题是合法的，不能一并拒掉。
        var exact = new PlaybackSnapshot("光", "陈粒", "playing", "kugou");
        Assert.True(PlaybackVerifier.Matches(exact, "光", "陈粒"));

        var partial = new PlaybackSnapshot("光年", "陈粒", "playing", "kugou");
        Assert.False(PlaybackVerifier.Matches(partial, "光", "陈粒"));
    }

    [Fact]
    public async Task Verifier_without_a_probe_cannot_claim_success()
    {
        var outcome = await PlaybackVerifier.WaitForPlayingAsync(null, "晴天", "周杰伦", 600);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public async Task Verifier_times_out_when_nothing_plays()
    {
        var probe = new StaticProbe(null);
        var outcome = await PlaybackVerifier.WaitForPlayingAsync(probe, "晴天", "周杰伦", 600);
        Assert.False(outcome.Ok);
        Assert.Equal(PickStatus.NotRunning, outcome.Status);
    }

    [Fact]
    public async Task Verifier_reports_the_wrong_song_when_something_else_plays()
    {
        var probe = new StaticProbe(new PlaybackSnapshot("稻香", "周杰伦", "playing", "QQMusic.exe"));
        var outcome = await PlaybackVerifier.WaitForPlayingAsync(probe, "晴天", "周杰伦", 600);
        Assert.False(outcome.Ok);
        Assert.Equal(PickStatus.WrongSong, outcome.Status);
        Assert.Contains("稻香", outcome.Detail!);
    }

    [Fact]
    public async Task Verifier_succeeds_once_the_target_song_is_playing()
    {
        var probe = new StaticProbe(new PlaybackSnapshot("晴天", "周杰伦", "playing", "QQMusic.exe"));
        var outcome = await PlaybackVerifier.WaitForPlayingAsync(probe, "晴天", "周杰伦", 600);
        Assert.True(outcome.Ok);
    }

    // ------------------------------------------------------------ 坐标拾取

    [Fact]
    public async Task Picker_refuses_when_the_search_box_is_not_calibrated()
    {
        var picker = new CoordinateMusicPicker(new AppDescriptor
        {
            AppId = "qqmusic",
            DisplayName = "QQ 音乐",
            ExeNames = new[] { "QQMusic" },
        });

        var outcome = await picker.PickAsync("晴天", "周杰伦");

        Assert.False(outcome.Ok);
        Assert.Equal(PickStatus.Unsupported, outcome.Status);
        Assert.Contains("标定", outcome.Detail!);
    }

    [Fact]
    public async Task Picker_refuses_a_fullscreen_window()
    {
        // 酷狗的歌手模式是全屏歌词页，里面没有搜索框，点了只会乱按
        var picker = new CoordinateMusicPicker(new AppDescriptor
        {
            AppId = "kugou",
            DisplayName = "酷狗音乐",
            ExeNames = new[] { "KuGou" },
            SearchBoxRatio = new[] { 0.3, 0.05 },
            ResultItemRatio = new[] { 0.2, 0.4 },
            BlockWhenScreenFilling = true,
        })
        {
            Locator = new StaticLocator(new TargetWindow(
                new IntPtr(1), 0, 0, 1920, 1032, "酷狗音乐",
                ScreenWidth: 1920, ScreenHeight: 1080)),
            Input = new RecordingInput(),
        };

        var outcome = await picker.PickAsync("晴天", "周杰伦");

        Assert.False(outcome.Ok);
        Assert.Contains("全屏歌词页", outcome.Detail!);
    }

    [Fact]
    public async Task Picker_clicks_the_calibrated_points_relative_to_the_window()
    {
        var input = new RecordingInput();
        var picker = new CoordinateMusicPicker(new AppDescriptor
        {
            AppId = "qqmusic",
            DisplayName = "QQ 音乐",
            ExeNames = new[] { "QQMusic" },
            SearchBoxRatio = new[] { 0.5, 0.05 },
            ResultItemRatio = new[] { 0.25, 0.45 },
            PickTimeoutMs = 500,
        })
        {
            Input = input,
            Locator = new StaticLocator(new TargetWindow(
                new IntPtr(1), 100, 200, 1000, 800, "QQ音乐")),
        };

        var outcome = await picker.PickAsync("晴天", "周杰伦",
            // 探针说"已经是目标歌了" → 点一行就收工
            new SequenceProbe(new PlaybackSnapshot("晴天", "周杰伦", "playing", "QQMusic.exe")));

        Assert.True(outcome.Ok);
        // 搜索框：窗口 (100,200) 1000x800 → 比例 (0.5,0.05) → 屏幕 (600, 240)
        Assert.Contains((600, 240), input.Clicks);
        // 结果行：比例 (0.25,0.45) → 屏幕 (350, 560)
        Assert.Contains((350, 560), input.DoubleClicks);
        // 中文歌名必须用粘贴，不能靠逐字敲
        Assert.Equal("晴天 周杰伦", input.Pasted);
        Assert.True(input.SelectedAll);
        Assert.True(input.EnterPressed);
    }

    [Fact]
    public async Task Picker_resizes_the_window_to_the_calibration_size()
    {
        // 坐标是按某个窗口尺寸量的；不归一的话，客户端被最大化后就会点到别的地方
        var input = new RecordingInput();
        var picker = new CoordinateMusicPicker(new AppDescriptor
        {
            AppId = "qqmusic",
            DisplayName = "QQ 音乐",
            ExeNames = new[] { "QQMusic" },
            SearchBoxRatio = new[] { 0.5, 0.05 },
            ResultItemRatio = new[] { 0.25, 0.45 },
            CalibrationWidth = 1226,
            CalibrationHeight = 800,
            PickTimeoutMs = 500,
        })
        {
            Input = input,
            Locator = new StaticLocator(new TargetWindow(
                new IntPtr(1), 100, 200, 1000, 800, "QQ音乐")),
        };

        await picker.PickAsync("晴天", "周杰伦");

        Assert.NotNull(input.Resized);
        Assert.Equal(1226, input.Resized!.Value.Width);
        Assert.Equal(800, input.Resized.Value.Height);
    }

    [Fact]
    public async Task Picker_tries_every_row_and_reports_when_none_matches()
    {
        // 客户端的列表布局会变，坐标只能点到"某一行"；所以要逐行试点、用媒体会话确认。
        // 全都没命中时必须如实报"没点到"，不能装作成功。
        var input = new RecordingInput();
        var picker = new CoordinateMusicPicker(new AppDescriptor
        {
            AppId = "qqmusic",
            DisplayName = "QQ 音乐",
            ExeNames = new[] { "QQMusic" },
            SearchBoxRatio = new[] { 0.5, 0.05 },
            ResultItemRatio = new[] { 0.25, 0.45 },
            ResultRowHeightRatio = 0.05,
            MaxResultRows = 3,
            PickTimeoutMs = 500,
        })
        {
            Input = input,
            Locator = new StaticLocator(new TargetWindow(
                new IntPtr(1), 100, 200, 1000, 800, "QQ音乐")),
        };

        var outcome = await picker.PickAsync("晴天", "周杰伦",
            new SequenceProbe(new PlaybackSnapshot("别的歌", "某人", "playing", "QQMusic.exe")));

        Assert.False(outcome.Ok);
        Assert.Equal(PickStatus.WrongSong, outcome.Status);
        Assert.Equal(3, input.DoubleClicks.Count);   // 试了 3 行都没中
    }

    // ------------------------------------------------------------ 测试替身

    private sealed class SequenceProbe(params PlaybackSnapshot?[] snapshots) : IPlaybackProbe
    {
        private int _i;
        public Task<PlaybackSnapshot?> ReadAsync(CancellationToken ct = default)
        {
            var s = _i < snapshots.Length ? snapshots[_i] : snapshots[^1];
            _i++;
            return Task.FromResult(s);
        }
    }

    private sealed class StaticProbe(PlaybackSnapshot? snap) : IPlaybackProbe
    {
        public Task<PlaybackSnapshot?> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(snap);
    }

    private sealed class StaticLocator(TargetWindow? window) : IWindowLocator
    {
        public TargetWindow? Find(AppDescriptor desc) => window;
    }

    private sealed class RecordingInput : IInputSimulator
    {
        public List<(int X, int Y)> Clicks { get; } = new();
        public List<(int X, int Y)> DoubleClicks { get; } = new();
        public string? Pasted { get; private set; }
        public bool SelectedAll { get; private set; }
        public bool EnterPressed { get; private set; }

        public bool Foreground(IntPtr hwnd) => true;
        public IntPtr ForegroundWindow() => new IntPtr(1);
        public void Restore(IntPtr hwnd) { }
        public void Resize(IntPtr hwnd, int width, int height) { Resized = (width, height); }
        public (int Width, int Height)? Resized { get; private set; }
        public void Click(int x, int y) => Clicks.Add((x, y));
        public void DoubleClick(int x, int y) => DoubleClicks.Add((x, y));
        public void SelectAll() => SelectedAll = true;
        public void PasteText(string text) => Pasted = text;
        public void PressEnter() => EnterPressed = true;
    }
}
