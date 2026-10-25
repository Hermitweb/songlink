using SongLink.Host.DeepLink;
using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 接管模式适配器（组合式）：
///   - 读元数据 / 传输控制 / 音量 → 委托 <c>transport</c>（通常是 SMTC，对任意在播 App 都有效）
///   - 搜索 → 本地曲库 + 各在线平台**聚合**，每条结果带 <c>source</c>/<c>sourceId</c>
///   - 点歌 → 按结果的 source 路由：本地歌走本地播放器，在线歌交给对应平台的播放策略
///
/// <para><b>为什么结果要带 sourceId</b>：搜索已经把歌名解析成平台内部 ID 了，点歌时直接用那个 ID，
/// 不再按歌名反查。反查意味着两次名称匹配，只要不一致就会**放错歌**——
/// 而用户以为点的是自己选的那首，这是最难被发现也最伤人的一类 bug。</para>
/// </summary>
public sealed class TakeoverAdapter : IPlayerAdapter, IAdapterDiagnostics, IDisposable
{
    private readonly IPlayerAdapter _transport;
    private readonly IPlayerAdapter _catalogue;
    private readonly IReadOnlyList<DeepLink.IMusicPlatform> _platforms;

    /// <summary>最近一次搜索结果，按 "歌名|歌手" 索引，供 <see cref="Pick"/> 回查 sourceId。</summary>
    private readonly Dictionary<string, SearchResultItem> _lastResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lastLock = new();

    public TakeoverAdapter(
        IPlayerAdapter transport,
        IPlayerAdapter catalogue,
        IEnumerable<DeepLink.IMusicPlatform> platforms)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _platforms = platforms?.ToArray() ?? Array.Empty<DeepLink.IMusicPlatform>();
    }

    /// <summary>已启用的在线平台名，界面提示用。</summary>
    public IReadOnlyList<string> PlatformNames => _platforms.Select(p => p.Name).ToArray();

    /// <summary>各在线平台当前**真实**能力，界面提示用。</summary>
    public IReadOnlyList<PlatformStatus> PlatformStatuses => _platforms
        .Select(p => new PlatformStatus(p.Kind, p.Name, p.Capability, p.LastFailureReason))
        .ToArray();

    /// <summary>
    /// 最近一次点歌失败的原因（中文，可直接显示给用户）。
    ///
    /// <para>为什么要有这个：以前点歌失败只有一句"APP_NOT_FOUND"，用户完全不知道是
    /// 没装客户端、没版权、还是要 VIP。现在把平台给出的**具体原因**透出来，
    /// 面板上直接显示，不再让用户猜。</para>
    /// </summary>
    public string? LastPickFailureReason { get; private set; }

    public string AppId => _transport.AppId;

    public AdapterCapability Caps =>
        _transport.Caps | (_catalogue.Caps & AdapterCapability.Search) | AdapterCapability.Pick;

    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default) => _transport.ReadNowPlaying(ct);

    public Task<bool> Play(CancellationToken ct = default) => _transport.Play(ct);
    public Task<bool> Pause(CancellationToken ct = default) => _transport.Pause(ct);
    public Task<bool> Next(CancellationToken ct = default) => _transport.Next(ct);
    public Task<bool> Prev(CancellationToken ct = default) => _transport.Prev(ct);
    public Task<bool> Stop(CancellationToken ct = default) => _transport.Stop(ct);
    public Task<double> GetVolume(CancellationToken ct = default) => _transport.GetVolume(ct);
    public Task<bool> SetVolume(double volume, CancellationToken ct = default) => _transport.SetVolume(volume, ct);

    /// <summary>
    /// 向在线平台实际索取的条数（抓取窗口）。
    /// </summary>
    /// <remarks>
    /// 必须抓得比展示多得多。实测网易云搜「晴天 周杰伦」：
    /// limit=30/50 都不含周杰伦原版，limit=100 才出现，且排在**第 74 位**
    /// （前面全是"深情版/钢琴版/DJ版"和"江南/十年/温柔"这类无关热门歌）。
    /// 平台自己的排序对"我要原版"这件事很不友好，所以靠**抓大窗口 + 本地排序**
    /// 才能把正确的歌捞到前面；抓完再截断，不把 100 条丢给面板。
    /// </remarks>
    private const int OnlineFetchWindow = 100;

    /// <summary>排序后返回给面板的条数上限。</summary>
    private const int DisplayLimit = 30;

    /// <summary>聚合搜索：本地曲库 + 各在线平台。任一源失败都不影响其它源。</summary>
    public async Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
    {
        var merged = new List<SearchResultItem>();

        try
        {
            var local = await _catalogue.Search(kw, ct);
            foreach (var item in local)
            {
                item.Source = "local";     // 显式标记来源
                merged.Add(item);
            }
        }
        catch { /* 本地库挂了不该影响在线搜索 */ }

        // 在线平台并发查，个别失败/超时不拖垮整体；抓大窗口给排序留出操作空间
        var tasks = _platforms
            .Select(p => SafeSearchAsync(p, kw, ct))
            .ToArray();
        foreach (var items in await Task.WhenAll(tasks))
            merged.AddRange(items);

        // 排序：各平台只做关键词模糊匹配，既不区分原版与翻版，还会混进无关热门歌。
        // 实测网易云搜「晴天 周杰伦」前几条全是改编、原版排到 74 位，不排必然点错歌。
        merged = DeepLink.ResultRanker.Rank(merged, kw).Take(DisplayLimit).ToList();

        lock (_lastLock)
        {
            _lastResults.Clear();
            foreach (var it in merged)
            {
                // 同一首歌可能多个来源都命中；保留先到的（本地优先），后到的同名不覆盖
                var key = Key(it.Title, it.Artist);
                if (!_lastResults.ContainsKey(key)) _lastResults[key] = it;
            }
        }

        return merged;
    }

    private static async Task<IReadOnlyList<SearchResultItem>> SafeSearchAsync(
        DeepLink.IMusicPlatform p, string kw, CancellationToken ct)
    {
        try
        {
            return await p.SearchAsync(kw, OnlineFetchWindow, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<SearchResultItem>(); }
    }

    /// <summary>
    /// 点歌。先用最近一次搜索结果里的 sourceId 精确播放；
    /// 回查不到（例如队列里点的是一首没搜过的歌）再退回本地播放。
    /// </summary>
    public async Task<bool> Pick(string title, string artist, CancellationToken ct = default)
    {
        SearchResultItem? hit = null;
        lock (_lastLock)
        {
            _lastResults.TryGetValue(Key(title, artist), out hit);
        }

        if (hit is not null && !string.IsNullOrEmpty(hit.SourceId) && hit.Source != "local")
        {
            var platform = _platforms.FirstOrDefault(
                p => string.Equals(p.Kind, hit.Source, StringComparison.OrdinalIgnoreCase));
            if (platform is not null)
            {
                try
                {
                    // 拿到搜索时确定的 ID 直接播，不再按歌名二次匹配
                    if (await platform.PlayAsync(hit, ct))
                    {
                        LastPickFailureReason = null;
                        return true;
                    }
                    // 平台说了具体原因就透传（没版权/要会员/客户端版本不支持…）
                    if (!string.IsNullOrWhiteSpace(platform.LastFailureReason))
                        LastPickFailureReason = platform.LastFailureReason;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { LastPickFailureReason = $"点歌失败：{ex.Message}"; }
            }
        }

        // 在线平台没能播（没装 App / 协议失效），退回本地曲库播放
        var localOk = await _catalogue.Pick(title, artist, ct);
        if (localOk) LastPickFailureReason = null;
        else if (string.IsNullOrWhiteSpace(LastPickFailureReason))
            LastPickFailureReason = "本地曲库里没有这首歌，在线平台也没能放出来";
        return localOk;
    }

    private static string Key(string title, string artist)
        => $"{DeepLink.HttpJson.Norm(title)}|{DeepLink.HttpJson.Norm(artist)}";

    public void Dispose()
    {
        (_transport as IDisposable)?.Dispose();
        (_catalogue as IDisposable)?.Dispose();
    }
}
