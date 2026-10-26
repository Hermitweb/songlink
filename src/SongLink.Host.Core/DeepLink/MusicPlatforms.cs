using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SongLink.Protocol;

namespace SongLink.Host.DeepLink;

// =============================================================================
// 网易云音乐
//   搜索：GET music.163.com/api/search/get（免登录，需 Referer）→ result.songs[].id
//   点歌：**orpheus:// 深链接实测无效**（下发后客户端不切歌，静默失效），
//         唯一有 Windows 实测证据的是 CDP 控制通道，见 <see cref="NeteaseCdpPlatform"/>。
//         所以这里如实返回失败，不去拼一个"看起来会work"的链接。
// =============================================================================
public sealed class NeteasePlatform : IMusicPlatform
{
    public string Kind => "netease";
    public string Name => "网易云音乐";
    public const string Referer = "https://music.163.com/";

    internal Action<string> Launch { get; init; } = LaunchUrl;

    /// <summary>
    /// 走"深链接"点歌。默认 false。
    ///
    /// <para><b>为什么默认关</b>：实测本机网易云桌面端收到 <c>orpheus://</c> 之后
    /// **不切歌、也不报错**（静默失败）。留一个"看起来能用、实际不响"的开关，
    /// 比没有这个开关更糟 —— 用户会以为点歌成功却没声音。所以默认关，
    /// 要用得显式开，且开了也只保证"命令发出去了"。</para>
    /// </summary>
    public bool AllowDeepLink { get; init; }

    public string? LastFailureReason { get; private set; }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0) return Array.Empty<SearchResultItem>();
        var json = await HttpJson.GetAsync(
            $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(kw)}&type=1&limit={Math.Clamp(limit, 1, 100)}",
            Referer, ct);
        if (json is null) return Array.Empty<SearchResultItem>();

        return NeteaseParser.Parse(json, limit);
    }

    public Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.SourceId))
        {
            LastFailureReason = "搜索结果缺少歌曲 ID，点歌失败";
            return Task.FromResult(false);
        }
        // SourceId 来自搜索接口的 JSON，拼进 orpheus:// 后要交给 ShellExecute：
        // 只认纯数字的歌曲 ID（网易云本来就是数字 ID），杜绝把换行/引号/别的协议头
        // 塞进 URL 注入到系统 Shell。
        if (item.SourceId.Length > 20 || !item.SourceId.All(char.IsAsciiDigit))
        {
            LastFailureReason = "歌曲 ID 格式异常，已拒绝拼装深链接";
            return Task.FromResult(false);
        }
        if (!AllowDeepLink)
        {
            LastFailureReason =
                "网易云的 orpheus:// 深链接实测不切歌（静默失效），已默认关闭该路径。" +
                "请改用 netease-cdp（客户端带 --remote-debugging-port 启动的 CDP 控制通道）。";
            return Task.FromResult(false);
        }
        try
        {
            Launch($"orpheus://song/{item.SourceId}");
            LastFailureReason = null;
            return Task.FromResult(true);
        }
        catch
        {
            LastFailureReason = "拉起网易云客户端失败（协议未注册或客户端没装）";
            return Task.FromResult(false);
        }
    }

    private static void LaunchUrl(string url) => SafeLaunch.DeepLink(url);

    /// <summary>解析搜索响应；独立成静态方法便于单测。</summary>
    internal static class NeteaseParser
    {
        internal static IReadOnlyList<SearchResultItem> Parse(string json, int limit)
        {
            var result = new List<SearchResultItem>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var songs = HttpJson.Path(doc.RootElement, "result", "songs");
                if (songs is not { ValueKind: System.Text.Json.JsonValueKind.Array }) return result;

                foreach (var s in songs.Value.EnumerateArray())
                {
                    var id = HttpJson.Scalar(s, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new SearchResultItem
                    {
                        Title = HttpJson.Scalar(s, "name") ?? "",
                        Artist = Artists(s, "artists"),
                        Album = HttpJson.Path(s, "album") is { } al ? HttpJson.Scalar(al, "name") : null,
                        // 网易云返回毫秒，协议里统一用秒
                        Dur = ParseDouble(HttpJson.Scalar(s, "duration")) / 1000.0,
                        Source = "netease",
                        SourceId = id,
                    });
                    if (result.Count >= limit) break;
                }
            }
            catch (System.Text.Json.JsonException) { }
            return result;
        }

        internal static string Artists(JsonElement song, string field)
        {
            if (!song.TryGetProperty(field, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return "";
            var names = arr.EnumerateArray().Select(a => HttpJson.Scalar(a, "name")).Where(s => !string.IsNullOrEmpty(s));
            return string.Join("/", names);
        }

        internal static double ParseDouble(string? s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}

// =============================================================================
// QQ 音乐
//   搜索：GET c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg（联想，免登录，实测可用）
//         → data.song.itemlist[].{id,mid,name,singer}
//         再用 c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?songmid=… 补时长/专辑/会员标记。
//         ⚠ 完整搜索（client_search_cp / musicu.fcg）实测已不可用：
//           client_search_cp 返回 500，musicu.fcg 返回 code 500003（风控/需登录态），
//           网页版搜索页是纯前端渲染、抓不到数据。所以只提供联想搜索。
//   点歌：桌面客户端**没有可用的注入通道**（实测：无 CDP、7 种命令行参数全部无效、
//         二进制里没有 playbysongid）。唯一可用的是驱动它自己的搜索框 → 见 IMusicPicker。
// =============================================================================
public sealed class QqMusicPlatform : IMusicPlatform
{
    public string Kind => "qq";
    public string Name => "QQ 音乐";
    public const string Referer = "https://y.qq.com/";

    /// <summary>联想搜索接口（免登录，实测可用）。</summary>
    internal const string SmartboxUrl =
        "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?key={0}&format=json";

    /// <summary>单曲详情接口（免登录，实测可用）。</summary>
    internal const string DetailUrl =
        "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?songmid={0}&format=json";

    /// <summary>在客户端里点歌的执行器（界面自动化）。没有它就只能搜不能点。</summary>
    public IMusicPicker? Picker { get; init; }

    /// <summary>播放校验器：点完用它确认"客户端真的在放这首歌"。</summary>
    public IPlaybackProbe? Probe { get; init; }

    /// <summary>传输控制：切歌后客户端是暂停的，靠它补一次"播放"。</summary>
    public IPlaybackControl? Control { get; init; }

    /// <summary>是否用详情接口补时长/专辑（多一次网络请求，但信息全得多）。</summary>
    public bool EnrichDetails { get; init; } = true;

    /// <summary>点歌后等待客户端起播的最长时间（毫秒）。</summary>
    public int VerifyTimeoutMs { get; init; } = 8000;

    public PlatformCapability Capability =>
        Picker is null ? PlatformCapability.SearchOnly : PlatformCapability.SearchAndPlay;

    public string? LastFailureReason { get; private set; }

    /// <summary>mid → 是否会员专属（搜索时从详情接口拿到的 pay 标记）。</summary>
    private readonly ConcurrentDictionary<string, bool> _vipOnly = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0) return Array.Empty<SearchResultItem>();

        var url = string.Format(SmartboxUrl, Uri.EscapeDataString(kw));
        var json = await HttpJson.GetAsync(url, Referer, ct);
        if (json is null) return Array.Empty<SearchResultItem>();

        var items = Parser.Parse(json, limit);
        if (EnrichDetails)
        {
            foreach (var it in items)
                await EnrichAsync(it, ct);
        }
        return items;
    }

    /// <summary>补时长/专辑，并记下这首歌是不是会员专属。</summary>
    private async Task EnrichAsync(SearchResultItem item, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.SourceId)) return;
        var json = await HttpJson.GetAsync(
            string.Format(DetailUrl, Uri.EscapeDataString(item.SourceId)), Referer, ct);
        if (json is null) return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var data = HttpJson.Path(doc.RootElement, "data");
            if (data is not { ValueKind: System.Text.Json.JsonValueKind.Array }) return;
            var first = data.Value.EnumerateArray().FirstOrDefault();

            if (HttpJson.Scalar(first, "title") is { Length: > 0 } title) item.Title = title;
            if (HttpJson.Scalar(first, "interval") is { Length: > 0 } iv &&
                NeteasePlatform.NeteaseParser.ParseDouble(iv) > 0)
                item.Dur = NeteasePlatform.NeteaseParser.ParseDouble(iv);
            if (HttpJson.Path(first, "album") is { } album &&
                HttpJson.Scalar(album, "name") is { Length: > 0 } albumName)
                item.Album = albumName;
            if (HttpJson.Path(first, "singer") is { } singers &&
                singers.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var names = singers.EnumerateArray()
                    .Select(s => HttpJson.Scalar(s, "name"))
                    .Where(s => !string.IsNullOrEmpty(s));
                var joined = string.Join("/", names);
                if (joined.Length > 0) item.Artist = joined;
            }

            // pay.pay_play == 1 → 会员专属；实测非会员点它会弹付费窗
            if (HttpJson.Path(first, "pay") is { } pay &&
                HttpJson.Scalar(pay, "pay_play") == "1")
                _vipOnly[item.SourceId] = true;
        }
        catch (System.Text.Json.JsonException) { }
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
    {
        if (Picker is null)
        {
            LastFailureReason =
                "QQ音乐 桌面客户端没有可用的点歌通道（实测：无 CDP、命令行参数无效），" +
                "本机只配置了搜索。需要开启「客户端界面点歌」才能点歌。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(item.SourceId))
        {
            LastFailureReason = "搜索结果缺少歌曲 ID，点歌失败";
            return false;
        }

        PickOutcome outcome;
        try
        {
            outcome = await Picker.PickAsync(item.Title, item.Artist, Probe, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastFailureReason = $"QQ音乐 点歌失败：{ex.Message}";
            return false;
        }

        if (!outcome.Ok)
        {
            LastFailureReason = outcome.Detail ?? "QQ音乐 点歌失败";
            return false;
        }

        // 点了不等于响了：用探针确认客户端确实在放这首歌
        var verified = await PlaybackVerifier.WaitForPlayingAsync(
            Probe, item.Title, item.Artist, VerifyTimeoutMs, ct, Control);
        if (verified.Ok)
        {
            LastFailureReason = null;
            return true;
        }

        var vip = _vipOnly.TryGetValue(item.SourceId, out var v) && v;
        LastFailureReason = vip
            ? $"《{item.Title}》是 VIP 会员歌曲，QQ音乐 没有放出来（实测会弹付费窗）。"
            : $"已点击《{item.Title}》，但客户端没有在放这首歌（{verified.Detail}）。可能是没有版权或被拦截。";
        return false;
    }

    internal static class Parser
    {
        internal static IReadOnlyList<SearchResultItem> Parse(string json, int limit)
        {
            var result = new List<SearchResultItem>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var list = HttpJson.Path(doc.RootElement, "data", "song", "itemlist");
                if (list is not { ValueKind: System.Text.Json.JsonValueKind.Array }) return result;

                foreach (var s in list.Value.EnumerateArray())
                {
                    // mid 是 H5 播放器/详情接口认的 id；数字 id 只在没有 mid 时兜底
                    var mid = HttpJson.Scalar(s, "mid");
                    var numId = HttpJson.Scalar(s, "id");
                    var id = !string.IsNullOrEmpty(mid) ? mid : numId;
                    if (string.IsNullOrEmpty(id)) continue;

                    result.Add(new SearchResultItem
                    {
                        Title = HttpJson.Scalar(s, "name") ?? "",
                        Artist = HttpJson.Scalar(s, "singer") ?? "",
                        Album = HttpJson.Scalar(s, "album_name"),
                        Dur = NeteasePlatform.NeteaseParser.ParseDouble(HttpJson.Scalar(s, "interval")),
                        Source = "qq",
                        SourceId = id,
                    });
                    if (result.Count >= limit) break;
                }
            }
            catch (System.Text.Json.JsonException) { }
            return result;
        }
    }
}

// =============================================================================
// 酷狗音乐
//   搜索：GET mobilecdn.kugou.com/api/v3/search/song → data.info[].hash（免登录，实测可用）
//   点歌：桌面客户端**没有可用的注入通道**（实测：workbuddy 桥需要客户端 ≥ 20.1.40，
//         本机 20.1.32 的二进制里字节级搜不到该协议；远程调试被编译版禁用；
//         `kugou://` 的可用格式未确证）。走界面自动化（IMusicPicker）。
// =============================================================================
public sealed class KugouPlatform : IMusicPlatform
{
    public string Kind => "kugou";
    public string Name => "酷狗音乐";
    public const string Referer = "https://www.kugou.com/";

    /// <summary>在客户端里点歌的执行器（界面自动化）。</summary>
    public IMusicPicker? Picker { get; init; }

    /// <summary>
    /// 用户自己提供的播放 URL 模板（须含 <c>{id}</c>）。
    ///
    /// <para><b>为什么保留这个口子</b>：酷狗的 <c>kugou://</c> 协议确实注册了
    /// （<c>KuGou.exe "%1"</c>），只是公开格式没确证。如果用户自己验证出了一种可用格式，
    /// 填进来就能用；<b>我们不猜</b> —— 猜错的后果是"放了一首别的歌，而用户以为是自己点的那首"，
    /// 比放不成糟糕得多。填了就优先用模板，不再走界面点歌。</para>
    /// </summary>
    public string? PlayTemplate { get; init; }

    /// <summary>播放校验器：点完用它确认"客户端真的在放这首歌"。</summary>
    public IPlaybackProbe? Probe { get; init; }

    /// <summary>传输控制：切歌后客户端是暂停的，靠它补一次"播放"。</summary>
    public IPlaybackControl? Control { get; init; }

    /// <summary>点歌后等待客户端起播的最长时间（毫秒）。</summary>
    public int VerifyTimeoutMs { get; init; } = 8000;

    public PlatformCapability Capability =>
        Picker is null && string.IsNullOrWhiteSpace(PlayTemplate)
            ? PlatformCapability.SearchOnly
            : PlatformCapability.SearchAndPlay;

    public string? LastFailureReason { get; private set; }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0) return Array.Empty<SearchResultItem>();
        var url = "http://mobilecdn.kugou.com/api/v3/search/song"
                + $"?format=json&keyword={Uri.EscapeDataString(kw)}&page=1&pagesize={Math.Clamp(limit, 1, 100)}";
        var json = await HttpJson.GetAsync(url, Referer, ct);
        if (json is null) return Array.Empty<SearchResultItem>();
        return Parser.Parse(json, limit);
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.SourceId))
        {
            LastFailureReason = "搜索结果缺少歌曲 ID，点歌失败";
            return false;
        }

        // 用户自己验证过的模板优先
        if (!string.IsNullOrWhiteSpace(PlayTemplate))
        {
            // 模板来自本地 takeover.json、ID 来自搜索接口 JSON，拼好的 URL 会被交给
            // ShellExecute：ID 只认字母数字和 _ -（酷狗的 hash 就是这些），
            // 拦住换行/引号/协议头注入；模板本身的 scheme 由 SafeLaunch 白名单把关。
            if (item.SourceId.Length > 64 || item.SourceId.Any(
                    c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            {
                LastFailureReason = "歌曲 ID 格式异常，已拒绝按模板播放";
                return false;
            }
            try
            {
                LaunchUrl(PlayTemplate.Replace("{id}", item.SourceId, StringComparison.Ordinal));
                LastFailureReason = null;
                return true;
            }
            catch (Exception ex)
            {
                LastFailureReason = $"按自定义模板播放失败：{ex.Message}";
                return false;
            }
        }

        if (Picker is null)
        {
            LastFailureReason =
                "酷狗桌面客户端没有可用的点歌通道（实测：本机 20.1.32 不支持社区的 workbuddy 控制桥，" +
                "远程调试被编译版禁用），本机只配置了搜索。需要开启「客户端界面点歌」才能点歌。";
            return false;
        }

        PickOutcome outcome;
        try
        {
            outcome = await Picker.PickAsync(item.Title, item.Artist, Probe, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastFailureReason = $"酷狗 点歌失败：{ex.Message}";
            return false;
        }

        if (!outcome.Ok)
        {
            LastFailureReason = outcome.Detail ?? "酷狗 点歌失败";
            return false;
        }

        var verified = await PlaybackVerifier.WaitForPlayingAsync(
            Probe, item.Title, item.Artist, VerifyTimeoutMs, ct, Control);
        if (verified.Ok)
        {
            LastFailureReason = null;
            return true;
        }

        LastFailureReason = $"已点击《{item.Title}》，但客户端没有在放这首歌（{verified.Detail}）。可能是没有版权或被拦截。";
        return false;
    }

    /// <summary>用系统默认方式打开 URL（自定义模板路径；scheme 由 SafeLaunch 白名单把关）。</summary>
    private static void LaunchUrl(string url) => SafeLaunch.DeepLink(url);

    internal static class Parser
    {
        internal static IReadOnlyList<SearchResultItem> Parse(string json, int limit)
        {
            var result = new List<SearchResultItem>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var info = HttpJson.Path(doc.RootElement, "data", "info");
                if (info is not { ValueKind: System.Text.Json.JsonValueKind.Array }) return result;
                foreach (var s in info.Value.EnumerateArray())
                {
                    var hash = HttpJson.Scalar(s, "hash");
                    if (string.IsNullOrEmpty(hash)) continue;
                    result.Add(new SearchResultItem
                    {
                        Title = HttpJson.Scalar(s, "songname_original") ?? "",
                        Artist = HttpJson.Scalar(s, "singername") ?? "",
                        Album = HttpJson.Scalar(s, "album_name"),
                        // 酷狗返回秒
                        Dur = NeteasePlatform.NeteaseParser.ParseDouble(HttpJson.Scalar(s, "duration")),
                        Source = "kugou",
                        SourceId = hash,
                    });
                    if (result.Count >= limit) break;
                }
            }
            catch (System.Text.Json.JsonException) { }
            return result;
        }
    }
}

// =============================================================================
// 酷我音乐 / 汽水音乐
//
//   设计原则（与 QQ音乐/酷狗 保持一致）：**不猜**。
//   · 酷我：桌面客户端会注册系统媒体会话（可用于"真在播"校验），但它的公开搜索
//     接口需要 `Secret` 头 + 一套会过期的 Cookie 签名流（本机 2026-10-26 实测返回
//     "The request is illegal!"）——按仓库一贯的"非官方接口随时失效"原则，不内置
//     一条随时会挂的搜索。搜索这里如实返回空并给出原因；**点歌**走通用的坐标
//     界面自动化（IMusicPicker），与 QQ/酷狗同一套，标定好 descriptors/kuwo.json
//     后即可用。
//   · 汽水：主要是移动端，未确证 Windows 桌面版注册媒体会话。点歌同样走坐标
//     界面自动化；没有可用的 SMTC 会话时探针为 null，会如实报"无法校验真在播"。
// =============================================================================

/// <summary>
/// 酷我音乐。点歌走坐标界面自动化（<see cref="Picker"/>）+ 系统媒体会话校验
/// （<see cref="Probe"/>）；搜索未接入免登录源，如实返回空。
/// </summary>
public sealed class KuwoPlatform : IMusicPlatform
{
    public string Kind => "kuwo";
    public string Name => "酷我音乐";

    /// <summary>在客户端里点歌的执行器（界面自动化，坐标驱动）。</summary>
    public IMusicPicker? Picker { get; init; }

    /// <summary>播放校验器：点完用它确认"客户端真的在放这首歌"。</summary>
    public IPlaybackProbe? Probe { get; init; }

    /// <summary>传输控制：切歌后客户端是暂停的，靠它补一次"播放"。</summary>
    public IPlaybackControl? Control { get; init; }

    /// <summary>点歌后等待客户端起播的最长时间（毫秒）。</summary>
    public int VerifyTimeoutMs { get; init; } = 8000;

    /// <summary>
    /// 没有免登录搜索源时如实降级为"仅点歌"。一旦日后接入可用搜索源，
    /// 把 <see cref="SearchProvider"/> 提供出去即自动升级为 SearchAndPlay。
    /// </summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<SearchResultItem>>>? SearchProvider { get; init; }

    public PlatformCapability Capability
    {
        get
        {
            var canPlay = Picker is not null;
            if (canPlay && SearchProvider is not null) return PlatformCapability.SearchAndPlay;
            if (SearchProvider is not null) return PlatformCapability.SearchOnly;
            if (canPlay) return PlatformCapability.PlayOnly;
            return PlatformCapability.None;
        }
    }

    public string? LastFailureReason { get; private set; }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        if (SearchProvider is null)
        {
            LastFailureReason =
                "酷我音乐的公开搜索接口需要 Secret+Cookie 签名流（会过期），" +
                "本轮未内置——搜索暂不可用。点歌走客户端界面自动化（需先标定 descriptors/kuwo.json）。";
            return Array.Empty<SearchResultItem>();
        }
        return await SearchProvider(keyword, limit, ct);
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            LastFailureReason = "搜索结果缺少歌名，酷我点歌失败";
            return false;
        }
        if (Picker is null)
        {
            LastFailureReason =
                "酷我音乐 只配置了搜索（或什么都没有），没有可用的点歌通道。" +
                "需要开启「客户端界面点歌」并完成坐标标定（descriptors/kuwo.json）才能点歌。";
            return false;
        }

        PickOutcome outcome;
        try
        {
            outcome = await Picker.PickAsync(item.Title, item.Artist, Probe, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastFailureReason = $"酷我 点歌失败：{ex.Message}";
            return false;
        }

        if (!outcome.Ok)
        {
            LastFailureReason = outcome.Detail ?? "酷我 点歌失败";
            return false;
        }

        var verified = await PlaybackVerifier.WaitForPlayingAsync(
            Probe, item.Title, item.Artist, VerifyTimeoutMs, ct, Control);
        if (verified.Ok)
        {
            LastFailureReason = null;
            return true;
        }

        LastFailureReason = $"已点击《{item.Title}》，但客户端没有在放这首歌（{verified.Detail}）。可能是没有版权或被拦截。";
        return false;
    }
}

/// <summary>
/// 汽水音乐（ByteDance）。点歌走坐标界面自动化（<see cref="Picker"/>）；
/// 未确证 Windows 桌面版注册系统媒体会话，探针为 null 时如实报"无法校验真在播"。
/// 搜索与 <see cref="KuwoPlatform"/> 同一原则：未接入免登录源，如实返回空。
/// </summary>
public sealed class QishuiPlatform : IMusicPlatform
{
    public string Kind => "qishui";
    public string Name => "汽水音乐";

    /// <summary>在客户端里点歌的执行器（界面自动化，坐标驱动）。</summary>
    public IMusicPicker? Picker { get; init; }

    /// <summary>播放校验器；Windows 桌面版未注册媒体会话时为 null。</summary>
    public IPlaybackProbe? Probe { get; init; }

    /// <summary>传输控制（可选）。</summary>
    public IPlaybackControl? Control { get; init; }

    /// <summary>点歌后等待客户端起播的最长时间（毫秒）。</summary>
    public int VerifyTimeoutMs { get; init; } = 8000;

    /// <summary>预留的免登录搜索源（当前未接入，保持 null）。</summary>
    public Func<string, int, CancellationToken, Task<IReadOnlyList<SearchResultItem>>>? SearchProvider { get; init; }

    public PlatformCapability Capability
    {
        get
        {
            var canPlay = Picker is not null;
            if (canPlay && SearchProvider is not null) return PlatformCapability.SearchAndPlay;
            if (SearchProvider is not null) return PlatformCapability.SearchOnly;
            if (canPlay) return PlatformCapability.PlayOnly;
            return PlatformCapability.None;
        }
    }

    public string? LastFailureReason { get; private set; }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        if (SearchProvider is null)
        {
            LastFailureReason =
                "汽水音乐的搜索没有稳定的免登录公开接口（主要是移动端，桌面版未确证）——" +
                "搜索暂不可用。点歌走客户端界面自动化（需先标定 descriptors/qishui.json）。";
            return Array.Empty<SearchResultItem>();
        }
        return await SearchProvider(keyword, limit, ct);
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            LastFailureReason = "搜索结果缺少歌名，汽水音乐点歌失败";
            return false;
        }
        if (Picker is null)
        {
            LastFailureReason =
                "汽水音乐 只配置了搜索（或什么都没有），没有可用的点歌通道。" +
                "需要开启「客户端界面点歌」并完成坐标标定（descriptors/qishui.json）才能点歌。";
            return false;
        }

        PickOutcome outcome;
        try
        {
            outcome = await Picker.PickAsync(item.Title, item.Artist, Probe, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastFailureReason = $"汽水音乐 点歌失败：{ex.Message}";
            return false;
        }

        if (!outcome.Ok)
        {
            LastFailureReason = outcome.Detail ?? "汽水音乐 点歌失败";
            return false;
        }

        // 没有探针时坐标点歌"点了但没法确认真在播"——如实上报
        if (Probe is null)
        {
            LastFailureReason = "汽水音乐 点歌通道没有可用的播放校验（桌面版未注册系统媒体会话），已点击但无法确认真在放。";
            return false;
        }

        var verified = await PlaybackVerifier.WaitForPlayingAsync(
            Probe, item.Title, item.Artist, VerifyTimeoutMs, ct, Control);
        if (verified.Ok)
        {
            LastFailureReason = null;
            return true;
        }

        LastFailureReason = $"已点击《{item.Title}》，但客户端没有在放这首歌（{verified.Detail}）。";
        return false;
    }
}

// =============================================================================
// 深链接安全启动器
// =============================================================================
/// <summary>
/// 把 URL 交给系统 Shell 之前先过 scheme 白名单。
///
/// <para><b>为什么要白名单</b>：拼进 URL 的内容并非全是可信的 —— 歌曲的
/// SourceId 来自搜索接口的 JSON，播放模板来自 takeover.json 配置。
/// 交给 Shell 的 URL 如果被拼装成 file:///、search-ms:、ms-msdt: 之类的协议，
/// 就等于让远端响应/配置文件决定"在用户机器上启动什么"。白名单外的
/// scheme 一律拒绝抛异常，保证深链接最多只能拉起一个音乐客户端协议。</para>
///
/// <para><b>为什么用 ShellExecuteW 而不是 Process.Start(UseShellExecute=true)</b>：
/// 二者底层是同一个系统调用；这里显式走 WinAPI 并传入 <see cref="Uri.AbsoluteUri"/>
/// （Uri 构造时已把控制字符/引号规整掉），加上前面的 scheme 白名单，
/// 整条链路没有任何"把外部字符串当命令拼"的环节。</para>
/// </summary>
internal static class SafeLaunch
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https",
        "orpheus",   // 网易云音乐
        "kugou",     // 酷狗音乐
        "qqmusic",   // QQ音乐
    };

    [System.Runtime.InteropServices.DllImport(
        "shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern System.IntPtr ShellExecuteW(
        System.IntPtr hwnd, string operation, string file,
        string? parameters, string? directory, int showCmd);

    /// <summary>SE_ERR_* 失败码都 ≤ 32，大于 32 才表示成功。</summary>
    internal static void DeepLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme.Length == 0 || !AllowedSchemes.Contains(uri.Scheme))
        {
            var scheme = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Scheme : "无法解析";
            throw new InvalidOperationException($"拒绝启动深链接：scheme 不在白名单（{scheme}）");
        }

        const int SW_SHOWNORMAL = 1;
        var hr = ShellExecuteW(System.IntPtr.Zero, "open", uri.AbsoluteUri, null, null, SW_SHOWNORMAL);
        if (hr.ToInt64() <= 32)
            throw new InvalidOperationException($"ShellExecuteW 启动深链接失败（SE_ERR 码 {hr.ToInt64()}）");
    }

    /// <summary>
    /// 启动一个本机可执行文件（带参数）。给 <see cref="NeteaseCdpPlatform"/> 这类
    /// "要把音乐客户端带调试参数拉起来"的场合用：调用方必须先自行校验
    /// <paramref name="file"/>（纯文件名 + 落在预期路径），parameters 只允许常量与
    /// 已校验过的整数，不允许任何来自网络/用户输入的字符串。
    /// </summary>
    internal static void Execute(string file, string parameters, string? workingDirectory)
    {
        const int SW_SHOWNORMAL = 1;
        var hr = ShellExecuteW(System.IntPtr.Zero, "open", file, parameters, workingDirectory, SW_SHOWNORMAL);
        if (hr.ToInt64() <= 32)
            throw new InvalidOperationException($"ShellExecuteW 启动进程失败（SE_ERR 码 {hr.ToInt64()}）");
    }
}
