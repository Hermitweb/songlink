namespace SongLink.Host.DeepLink;

/// <summary>
/// "点完歌之后，客户端到底有没有在放这首歌"的判定。
///
/// <para>为什么单独抽出来：网易云的实测教训 —— 深链接下发后**切歌成功但状态是 Stop**（没版权），
/// 那种情况如果算"点歌成功"，用户看到的就是"点歌成功却没声音"，这是最难解释的一类失败。
/// QQ音乐/酷狗走界面自动化更容易点歪（点到了"播放全部"、弹了付费窗），同样需要一道校验。</para>
///
/// <para>判据：系统媒体会话（SMTC）里出现了目标歌名，**且**状态是在播。
/// 歌名用"归一化后互相包含"来比，因为客户端显示的标题常常带后缀
/// （如《晴天 (Live)》《若月亮没来 (若是月亮还没来)》、QQ音乐还会把连播列表拼成一个长标题）。</para>
/// </summary>
public static class PlaybackVerifier
{
    /// <summary>
    /// 轮询探针直到目标歌真的在播，或超时。
    /// </summary>
    /// <param name="probe">播放探针；为 null 时无法校验，直接返回失败（不假装成功）。</param>
    /// <param name="title">目标歌名。</param>
    /// <param name="artist">目标歌手（可空）。</param>
    /// <param name="timeoutMs">最长等待时间。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="control">
    /// 传输控制（可选）。实测 QQ音乐 界面点歌后切歌成功但播放器仍是"暂停"，
    /// 这时补发一次"播放"指令才真的有声音。没有它就会把"歌切对了但没响"判成失败。
    /// </param>
    public static async Task<PickOutcome> WaitForPlayingAsync(
        IPlaybackProbe? probe,
        string? title,
        string? artist,
        int timeoutMs,
        CancellationToken ct = default,
        IPlaybackControl? control = null)
    {
        if (probe is null)
            return new PickOutcome(PickStatus.Failed, "没有接入播放校验通道，无法确认是否真的响起来了");

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
        PlaybackSnapshot? last = null;
        var nudges = 0;   // 补"播放"的次数（客户端在切歌瞬间会忽略，得多试几次）

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                last = await probe.ReadAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { last = null; }

            if (last is not null && Matches(last, title, artist))
            {
                if (last.IsPlaying) return PickOutcome.Started();

                // 歌切对了但没在播 → 补"播放"（实测客户端在切歌瞬间会忽略第一次，要多试）
                if (nudges < 3 && control is not null)
                {
                    nudges++;
                    try { await control.PlayAsync(ct); } catch { /* 补发失败就等超时 */ }
                }
            }

            await Task.Delay(400, ct);
        }

        if (last is null)
            return new PickOutcome(PickStatus.NotRunning, "系统里没有正在播放的媒体会话");
        if (!Matches(last, title, artist))
            return new PickOutcome(PickStatus.WrongSong, $"在放的是《{last.Title}》");
        return new PickOutcome(PickStatus.Failed, $"当前播放状态是 {last.Status}（已切到目标歌，但没有起播）");
    }

    /// <summary>标题（必要时加歌手）是否匹配当前在放的曲子。</summary>
    public static bool Matches(PlaybackSnapshot snap, string? title, string? artist)
    {
        if (!string.IsNullOrWhiteSpace(title) && !TitleMatches(snap.Title, title))
            return false;

        if (string.IsNullOrWhiteSpace(artist)) return true;

        var want = HttpJson.Norm(artist);
        if (want.Length == 0) return true;
        var have = HttpJson.Norm(snap.Artist);
        // 客户端可能显示 "周杰伦 / 林俊杰" 或 "王宇宙Leto、乔浚丞"，取包含即可
        return have.Length == 0 || have.Contains(want, StringComparison.Ordinal);
    }

    /// <summary>标题匹配：完全相同，或（目标名较长时）互为子串。</summary>
    public static bool TitleMatches(string? playing, string? want)
    {
        var p = HttpJson.Norm(playing);
        var w = HttpJson.Norm(want);
        if (w.Length == 0) return true;
        if (p.Length == 0) return false;
        if (p == w) return true;
        // 单字歌名太容易误判（"光" 能命中一堆），至少要两个字
        if (w.Length < 2) return false;
        return p.Contains(w, StringComparison.Ordinal) || w.Contains(p, StringComparison.Ordinal);
    }
}
