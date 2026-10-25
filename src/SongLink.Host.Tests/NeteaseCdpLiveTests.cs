using SongLink.Host.DeepLink;
using SongLink.Protocol;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// 网易云 CDP 接管的**真机联调**测试。
///
/// <para>默认跳过（返回即通过），因为它依赖本机装了网易云桌面客户端、
/// 且客户端是**带调试端口**启动的。只有显式设置环境变量
/// <c>SONGLINK_LIVE_CDP=1</c> 才会真正跑。</para>
///
/// <para>跑之前先这样启动客户端，否则单实例客户端会忽略新参数、端口开不出来：</para>
/// <code>
/// 关闭网易云 → cloudmusic.exe --remote-debugging-address=127.0.0.1 --remote-debugging-port=9223
/// </code>
/// </summary>
public class NeteaseCdpLiveTests
{
    private static bool LiveEnabled =>
        Environment.GetEnvironmentVariable("SONGLINK_LIVE_CDP") == "1";

    private static bool ClientReady => CdpClient.HasPageTarget(NeteaseCdpPlatform.DefaultCdpPort);

    [Fact]
    public async Task Live_client_exposes_a_control_channel_and_can_read_state()
    {
        if (!LiveEnabled) return;      // 默认不跑
        if (!ClientReady)
        {
            // 端口没开就明确说明，不静默通过
            Assert.Fail(
                "网易云没有开启 CDP 控制通道。请先关闭网易云，再用 " +
                "--remote-debugging-port=9223 启动它，然后重跑本测试。");
        }

        var p = new NeteaseCdpPlatform();
        // 读状态：走一次 Search 确认连通（搜索走公开接口，这里只验证平台对象可用）
        var items = await p.SearchAsync("晴天", 3);
        Assert.NotNull(items);
    }

    [Fact]
    public async Task Live_pick_switches_the_current_track_and_starts_playing()
    {
        if (!LiveEnabled) return;
        if (!ClientReady)
            Assert.Fail("网易云没有开启 CDP 控制通道。");

        var p = new NeteaseCdpPlatform();

        // 用一首**账号有版权**的歌来验证完整闭环（实测周杰伦《晴天》在网易云无版权，
        // 那种情况客户端会接受"切歌"但状态停在 Stop —— 那是版权问题，不是功能问题）。
        var licensed = await FindPlayableSongAsync(p);
        Assert.NotNull(licensed);

        var item = new SearchResultItem
        {
            Title = licensed!.Value.Title, Artist = licensed.Value.Artist,
            Source = "netease-cdp", SourceId = licensed.Value.Id,
        };

        var ok = await p.PlayAsync(item);
        var state = await ReadState(p);

        Assert.NotNull(state);
        var s = state!.Value;

        // 0) 先看成功与否，失败时把真实原因带出来（否则后面的断言会盖住原因）
        Assert.True(ok, "有版权的歌应当起播成功: " + (p.LastFailureReason ?? "(无原因)"));

        // 1) 当前曲目确实变成了目标曲目
        Assert.Equal(long.Parse(licensed.Value.Id), s.CurrentId);
        Assert.Equal(licensed.Value.Title, s.CurrentName);
        // 2) 真的在播（playingState == 2）
        Assert.True(s.IsPlaying, "客户端状态应为 Playing");
    }

    /// <summary>挑一首有版权的歌：先看客户端自己的播放列表（那肯定是用户自己放的、能播），
    /// 列表为空时退回一首已实测可播的歌。</summary>
    private static async Task<(string Id, string Title, string Artist)?> FindPlayableSongAsync(NeteaseCdpPlatform p)
    {
        try
        {
            using var cdp = new CdpClient(NeteaseCdpPlatform.DefaultCdpPort);
            var mods = await DiscoverAsync(cdp);
            if (mods is not null)
            {
                var js = ("(()=>{const s=__gpt_cm_require__(" + mods + ").a.getStore();"
                        + "const l=s.playing.curPlayingList||[];"
                        + "return JSON.stringify(l.slice(0,5).map(x=>({id:String(x.resourceId),"
                        + "name:(x.track||{}).name||'',artist:''})));})()");
                var raw = await cdp.EvaluateAsync(js);
                if (!string.IsNullOrEmpty(raw))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var id = item.GetProperty("id").GetString();
                        if (string.IsNullOrEmpty(id) || id == "0") continue;
                        return (id, item.GetProperty("name").GetString() ?? "", "");
                    }
                }
            }
        }
        catch { }

        // 队列是空的（可能被清过），用一首实测可播的歌兜底
        return ("208958", "越来越不懂", "蔡健雅");
    }

    private static async Task<(long CurrentId, string CurrentName, bool IsPlaying)?> ReadState(
        NeteaseCdpPlatform p)
    {
        using var cdp = new CdpClient(NeteaseCdpPlatform.DefaultCdpPort);
        var target = cdp.FindPageTarget();
        if (target is null) return null;
        var raw = await cdp.EvaluateAsync(
            "(()=>{const s=window.__gpt_cm_require__;return s?'ok':'no'})()");
        if (raw is null) return null;

        // 复用平台内部的读取逻辑：这里直接用反射不便，改为重新发现模块并读
        var mods = await DiscoverAsync(cdp);
        if (mods is null) return null;
        // 原始字符串里带 {} 会和插值语法打架，改用普通拼接
        var js = "(()=>{const s=__gpt_cm_require__(" + mods + ").a.getStore();const p=s.playing;"
               + "return JSON.stringify({id:p.curPlaying?Number(p.curPlaying.resourceId||0):0,"
               + "name:p.curPlaying?((p.curPlaying.track||{}).name||''):'',"
               + "playing:Number(p.playingState||0)===2});})()";
        var json = await cdp.EvaluateAsync(js);
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        return (r.GetProperty("id").GetInt64(), r.GetProperty("name").GetString() ?? "",
                r.GetProperty("playing").GetBoolean());
    }

    private static async Task<int?> DiscoverAsync(CdpClient cdp)
    {
        await cdp.EvaluateAsync(
            "webpackJsonp.push([[987660],{987660:function(module,exports,require){"
            + "window.__gpt_cm_require__=require;}},[[987660]]]);true");
        // **复用页面缓存**：客户端里同一批模块可能不止一个带 getStore，
        // 重新扫描会挑到与生产代码不同的那个，读到另一个 store 的状态。
        var raw = await cdp.EvaluateAsync(
            "(()=>{if(window.__gpt_cm_mods__&&window.__gpt_cm_mods__.store)"
            + "return String(window.__gpt_cm_mods__.store);"
            + "const rq=window.__gpt_cm_require__,m=rq.m||{};"
            + "for(const id of Object.keys(m)){try{const ex=rq(Number(id));"
            + "if(ex&&ex.a&&typeof ex.a.getStore==='function'&&typeof ex.a.getDispatch==='function')"
            + "return String(id);}catch(e){}}return 'null';})()");
        return int.TryParse(raw, out var v) ? v : null;
    }
}
