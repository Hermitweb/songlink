using System.Text.Json;
using SongLink.Protocol;

namespace SongLink.Host.DeepLink;

/// <summary>
/// 一个音乐平台的「搜索 + 点歌」能力。
///
/// **为什么搜索要直接返回歌曲 ID**：早先的设计是"先按歌名搜到一条 → 点歌时再按歌名反查 ID"，
/// 这中间隔了一次名称匹配，只要两次结果不一致，就会**放错歌**——而用户以为点的是自己选的那首。
/// 现在搜索结果自带 <see cref="SearchResultItem.SourceId"/>，点歌时拿 ID 直接播，
/// 全程不再做名称匹配，从根上杜绝"点错歌"。
/// </summary>
public interface IMusicPlatform
{
    /// <summary>配置里的标识，同时也是 <see cref="SearchResultItem.Source"/> 的值。</summary>
    string Kind { get; }

    /// <summary>给用户看的名字。</summary>
    string Name { get; }

    /// <summary>
    /// 在线搜索。返回的每条都带 <c>Source</c>=<see cref="Kind"/> 与 <c>SourceId</c>。
    /// 接口挂了就返回空列表 —— 搜索只是增强，挂了不该让整个面板失效。
    /// </summary>
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default);

    /// <summary>
    /// 播放一个搜索结果。返回 false 表示失败/不适用，由调用方如实报错。
    /// </summary>
    Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default);

    /// <summary>
    /// 这个平台当前**真实**能做到哪一步（默认按"能搜能播"）。
    ///
    /// <para>做成接口默认成员，是为了不给每个实现都加一遍；实现类只要自己声明同名属性
    /// 就会覆盖默认值。不写这一层的实现者（测试里的假平台）行为不变。</para>
    /// </summary>
    PlatformCapability Capability => PlatformCapability.SearchAndPlay;

    /// <summary>
    /// 最近一次点歌失败的原因（中文，可直接显示给用户）。成功时为 null。
    /// </summary>
    string? LastFailureReason => null;
}

/// <summary>三个平台共用的 HTTP / JSON 小工具。</summary>
internal static class HttpJson
{
    internal static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    // 禁用代理：本机的系统代理（127.0.0.1:7890）会把这些直连请求打断
    internal static HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>带 Referer / UA 发 GET；任何异常都吞掉返回 null（搜索失败不该拖垮面板）。</summary>
    internal static async Task<string?> GetAsync(string url, string referer, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", referer);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SongLink/1.0");
            using var resp = await Client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>读可能是字符串、也可能是数字的标量。</summary>
    internal static string? Scalar(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    /// <summary>逐层取对象，链上任一层缺失就返回 null，不抛。</summary>
    internal static JsonElement? Path(JsonElement root, params string[] steps)
    {
        var cur = root;
        foreach (var s in steps)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(s, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>归一化：去空白、转小写，让 "Don't Stop" / "dontstop" 能互相比对。</summary>
    internal static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
