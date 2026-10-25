using System.Diagnostics;
using System.Text.Json;
using SongLink.Protocol;

namespace SongLink.Host.DeepLink;

/// <summary>
/// 网易云音乐「接管」适配器：走**桌面客户端自己的控制通道**（CDP），
/// 而不是靠 URL scheme 猜。
///
/// <para><b>为什么不用 URL scheme</b>：客户端注册的 <c>orpheus://</c> 协议实际格式是
/// <c>orpheus://&lt;base64({"type":"song","id":"...","cmd":"play"})&gt;</c>（不是 <c>orpheus://song/{id}</c>），
/// 但实测在本机客户端版本（3.1.41）上调用它**不会切歌**，也不报错 —— 属于静默失效。
/// 走 CDP 至少能得到明确反馈：改没改成、当前在放哪首，一目了然。</para>
///
/// <para><b>前置条件</b>：客户端必须带 <c>--remote-debugging-port=9223</c> 启动。
/// 若客户端已在运行但没带该参数，需要先关闭再用带参数的方式启动 ——
/// 单实例客户端会忽略后启动实例的参数，这也是为什么这里提供 <see cref="EnsureRunning"/>。</para>
/// </summary>
public sealed class NeteaseCdpPlatform : IMusicPlatform
{
    public const int DefaultCdpPort = 9223;

    public string Kind => "netease-cdp";
    public string Name => "网易云音乐(接管)";

    /// <summary>客户端可执行文件名。</summary>
    public string ExeName { get; init; } = "cloudmusic.exe";

    /// <summary>CDP 调试端口。</summary>
    public int CdpPort { get; init; } = DefaultCdpPort;

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(string keyword, int limit, CancellationToken ct = default)
    {
        // 搜索仍走公开接口：客户端自带的搜索通道在 3.1.41 上签名与社区实现不同，未验证；
        // 公开接口已实测可用（配合 ResultRanker 能把原版排到前面）。
        return await new NeteasePlatform().SearchAsync(keyword, limit, ct);
    }

    public async Task<bool> PlayAsync(SearchResultItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.SourceId)) return false;
        if (!int.TryParse(item.SourceId, out var songId) || songId <= 0) return false;

        try
        {
            if (!EnsureRunning()) return false;
            using var cdp = new CdpClient(CdpPort);
            if (cdp.FindPageTarget() is null) return false;

            var mods = await DiscoverModulesAsync(cdp, ct);
            if (mods is null) return false;

            var tracks = await SongDetail.FetchAsync(new[] { songId }, ct);
            if (tracks.Count == 0) return false;

            // offset 要跟客户端的既有约定对齐：它是"从哪一首开始播"的序号，
            // 空队列时应为 -1（表示从头）。填 0 会被客户端的 middleware 拒绝
            // （dva 报 "Middleware cannot return a Promise"，Redux error #8）。
            var before = await ReadStateAsync(cdp, mods.Store, ct);
            var offset = before is null ? -1 : Math.Max(-1, before.ListLen - 1);

            // 1) 把队列换成目标曲目，并把它设为当前曲目
            var payload = new
            {
                tracks,
                from = new
                {
                    text = "SongLink", href = "", resourceType = "track", scene = "search",
                    fromInfo = new
                    {
                        originalScene = "search", originalResourceType = "track",
                        computeSourceResourceType = "track", sourceData = new { },
                    },
                },
                options = new { clear = true, play = true, playId = songId, offset },
                triggerScene = "search",
                triggerAction = "playAll",
            };
            await DispatchAsync(cdp, mods.Store, "playing/play", payload, ct);

            // 注意：**不要**在这里追加 playing/resume —— 实测它会打断上面的播放动作
            // （客户端还没来得及起播就被 resume 打断，结果当前曲目都没换成功）。
            // "playing/play" 自身带 options.play=true，已足够让有版权的歌起播。

            // 3) 回读状态确认，而不是"下发成功就算数"
            var state = await ReadStateAsync(cdp, mods.Store, ct);
            if (state is null) return false;

            if (state.CurrentId != songId)
            {
                LastFailureReason = "没能把歌曲切到当前曲目";
                return false;
            }
            if (state.IsPlaying) return true;      // playingState == 2 (Playing)

            // 切歌成功但没起播 —— 实测这几乎都是**版权问题**：
            // 客户端能接受"当前曲目"变更，但拿不到播放授权，状态停在 Stop(0)。
            // 此时必须如实报失败，不能让界面显示"点歌成功"而实际一片安静。
            LastFailureReason = "已切到该曲目但未能播放（这首歌在网易云可能没有版权）";
            return false;
        }
        catch (Exception ex)
        {
            // 任何异常都如实失败，让上层提示"点歌失败"，不要假装成功。
            // 异常信息带上，便于排查（之前这里只有一句笼统的话，掩盖了真实原因）。
            LastFailureReason = "与网易云客户端通信失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>上一次点歌失败的原因（成功时为 null），供上层给出更准确的提示。</summary>
    public string? LastFailureReason { get; private set; }

    /// <summary>读取客户端当前的播放状态（当前曲目 / 是否在放 / 队列长度 / 播放模式）。</summary>
    public async Task<PlayerState?> ReadStateAsync(CancellationToken ct = default)
    {
        try
        {
            if (!CdpClient.HasPageTarget(CdpPort)) return null;
            using var cdp = new CdpClient(CdpPort);
            if (cdp.FindPageTarget() is null) return null;
            var mods = await DiscoverModulesAsync(cdp, ct);
            if (mods is null) return null;
            return await ReadStateAsync(cdp, mods.Store, ct);
        }
        catch { return null; }
    }

    /// <summary>客户端播放状态快照。</summary>
    public sealed record PlayerState(long CurrentId, string CurrentName, bool IsPlaying, int ListLen, string Mode);

    // ------------------------------------------------------------------ CDP 内部

    private sealed record Mods(int Store, int? Search);

    /// <summary>
    /// 运行时**自发现**内部模块 id。
    /// </summary>
    /// <remarks>
    /// 网易云升级会改 webpack 模块编号（社区实现测的 3.1.24 是 12/14，本机 3.1.41 是 8/15），
    /// 所以绝不能硬编码数字 —— 改成扫模块表、认导出特征（有没有 getStore/getDispatch）。
    /// </remarks>
    private static async Task<Mods?> DiscoverModulesAsync(CdpClient cdp, CancellationToken ct)
    {
        // shim **必须幂等**：重复 webpackJsonp.push 一个新 chunk 会改动模块表，
        // 触发客户端的模块热更新，进而把 dva 的中间件链搞乱（派发时抛
        // "Middleware cannot return a Promise"，Redux error #8）。所以只在没注入过时才 push。
        const string shim = """
        (()=>{ if(!window.__gpt_cm_require__){
          webpackJsonp.push([[987660],{987660:function(module,exports,require){
            window.__gpt_cm_require__=require;}},[[987660]]]);
        } return true;})()
        """;

        await cdp.EvaluateAsync(shim, false, ct);
        // 注入新 chunk 后客户端需要一点时间消化，给它一个呼吸的窗口再派发指令。
        await Task.Delay(300, ct);

        const string discover = """
        (()=>{
          // 页面级缓存：**只扫一次**。反复 require 一堆候选模块是有副作用的
          //（模块会被真正执行，可能触发 dva 派发），进而把中间件链搞乱。
          if (window.__gpt_cm_mods__) return JSON.stringify(window.__gpt_cm_mods__);
          const rq = window.__gpt_cm_require__;
          if (!rq) return JSON.stringify({store:null, search:null});
          const m = rq.m || {};
          const cands = Object.keys(m).filter(id => {
            try { return /getStore|getDispatch/.test(String(m[id])); } catch(e) { return false; }
          });
          let store = null, search = null;
          for (const id of cands) {
            try {
              const ex = rq(Number(id));
              if (ex && ex.a && typeof ex.a.getStore === 'function'
                  && typeof ex.a.getDispatch === 'function') { store = Number(id); break; }
            } catch(e) {}
          }
          window.__gpt_cm_mods__ = { store: store, search: search };
          return JSON.stringify(window.__gpt_cm_mods__);
        })()
        """;

        var raw = await cdp.EvaluateAsync(discover, false, ct);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var store = doc.RootElement.TryGetProperty("store", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32() : 0;
            if (store == 0) return null;
            var search = doc.RootElement.TryGetProperty("search", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? sc.GetInt32() : (int?)null;
            return new Mods(store, search);
        }
        catch { return null; }
    }

    private static async Task DispatchAsync(CdpClient cdp, int storeMod, string action, object payload, CancellationToken ct)
    {
        // **必须包成 {type, payload}** —— 直接把 payload 当 action 派发会缺 type 字段，
        // dva 的中间件会因此炸掉（Redux error #8 "Middleware cannot return a Promise"）。
        var json = JsonSerializer.Serialize(new
        {
            type = action,
            payload,
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // 不转义非 ASCII：System.Text.Json 默认把中文变成 \uXXXX，客户端有按歌名匹配的逻辑，转义后可能对不上。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var expr = "(async()=>{"
                 + $"const d=__gpt_cm_require__({storeMod}).a.getDispatch();"
                 + "await d(" + json + ");return true})()";
        await cdp.EvaluateAsync(expr, true, ct);
    }

    private static async Task<PlayerState?> ReadStateAsync(CdpClient cdp, int storeMod, CancellationToken ct)
    {
        // 注意：这里**不能**用 string.Format —— JS 里满是 { }，会被当成格式化占位符，
        // 然后去 int.Parse("playingState: p.playingState,...") 直接抛异常。用字符串替换。
        const string expr = """
        (()=>{const s=__gpt_cm_require__(__MOD__).a.getStore();const p=s.playing;
        return JSON.stringify({
          currentId: p.curPlaying ? Number(p.curPlaying.resourceId||0) : 0,
          currentName: p.curPlaying ? ((p.curPlaying.track||{}).name||'') : '',
          playingState: Number(p.playingState||0),
          listLen: (p.curPlayingList||[]).length,
          mode: String(p.playingMode||'')});})()
        """;
        var js = expr.Replace("__MOD__", storeMod.ToString());
        var raw = await cdp.EvaluateAsync(js, false, ct);
        if (string.IsNullOrEmpty(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;
        var state = r.GetProperty("playingState").GetInt32();
        // 网易云 playingState 枚举：-1=End, 0=Stop, 1=Pause, 2=Playing
        // 只有 2 才算"真的在放"；1 是暂停，0 是没播（常见于无版权）。
        return new PlayerState(
            r.GetProperty("currentId").GetInt64(),
            r.GetProperty("currentName").GetString() ?? "",
            state == 2,
            r.GetProperty("listLen").GetInt32(),
            r.GetProperty("mode").GetString() ?? "");
    }

    // ------------------------------------------------------------------ 客户端进程

    /// <summary>
    /// 确保客户端带调试端口在跑：没在跑就带参数启动；在跑但没有 CDP 通道则返回 false
    /// （单实例客户端会忽略后启动实例的参数，必须由用户先关掉再启动）。
    /// </summary>
    public bool EnsureRunning()
    {
        if (CdpClient.HasPageTarget(CdpPort)) return true;

        // ExeName 来自 takeover.json 配置：必须是**纯文件名**（不允许 \ / .. ），
        // 否则配置里写一个相对路径/穿越序列就能把下面的启动指到任意可执行文件。
        if (string.IsNullOrWhiteSpace(ExeName) || ExeName != Path.GetFileName(ExeName))
            return false;
        // CdpPort 同样来自配置，拼进调试端口参数前限定合法端口区间。
        if (CdpPort is < 1 or > 65535) return false;

        var exe = ResolveExe();
        if (exe is null) return false;
        // 双保险：解析结果必须落在预期的文件名上（防 ResolveExe 未来改动引入偏差）
        if (!Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            return false;

        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Any();
        if (running)
        {
            // 已在运行但没开调试端口：直接返回 false，让上层提示用户重启客户端
            return false;
        }

        try
        {
            // 启动参数全部是常量 + 已校验区间(1..65535)的端口整数，不含任何来自
            // 网络或用户输入的自由文本；exe 已通过上面的纯文件名与落点校验。
            SafeLaunch.Execute(
                exe,
                $"--remote-debugging-address=127.0.0.1 --remote-debugging-port={CdpPort}",
                Path.GetDirectoryName(exe));
            // 等客户端起来并开端口
            for (var i = 0; i < 20; i++)
            {
                Thread.Sleep(500);
                if (CdpClient.HasPageTarget(CdpPort)) return true;
            }
        }
        catch { }
        return false;
    }

    private string? ResolveExe()
    {
        // 优先用正在运行的实例的真实路径
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))
            .Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (running is not null) return running;

        foreach (var guess in new[]
                 {
                     $@"C:\Program Files (x86)\Netease\CloudMusic\{ExeName}",
                     $@"C:\Program Files\Netease\CloudMusic\{ExeName}",
                     $@"D:\CloudMusic\{ExeName}",
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Netease", "CloudMusic", ExeName),
                 })
            if (File.Exists(guess)) return guess;
        return null;
    }
}

/// <summary>歌曲详情查询（网易云公开接口）。</summary>
public static class SongDetail
{
    // 禁用代理：本机的系统代理（127.0.0.1:7890）会把这些直连请求打断
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    public static async Task<IReadOnlyList<JsonElement>> FetchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        var result = new List<JsonElement>();
        try
        {
            var q = Uri.EscapeDataString("[" + string.Join(",", ids) + "]");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://music.163.com/api/song/detail?ids={q}");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return result;
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException je)
            {
                throw new InvalidOperationException(
                    "歌曲详情接口返回的不是 JSON: " + je.Message + " | 原文前200字符: "
                    + (body.Length <= 200 ? body : body[..200]), je);
            }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (var s in songs.EnumerateArray()) result.Add(s.Clone());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return result;
    }
}
