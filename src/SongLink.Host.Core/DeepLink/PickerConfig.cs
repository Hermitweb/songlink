using System.Text.Json;

namespace SongLink.Host.DeepLink;

/// <summary>
/// 接管模式下要启用的在线平台。配置文件：<c>%LOCALAPPDATA%\SongLink\takeover.json</c>。
///
/// 示例：
/// ```json
/// {
///   "acknowledgeRisk": true,
///   "platforms": [
///     { "kind": "netease", "enabled": true },
///     { "kind": "qq",      "enabled": true },
///     { "kind": "kugou",   "enabled": true, "playTemplate": "kugou://…" }
///   ]
/// }
/// ```
///
/// <para><b>为什么需要 acknowledgeRisk</b>：三家的在线搜索与点歌都依赖**非官方接口/私有协议**，
/// 随时可能失效，且涉及服务条款与账号风险。与其让用户稀里糊涂就跑上了，
/// 不如要求显式勾一次。默认 false —— 不写这个开关，一个在线平台都不启用。</para>
///
/// <para><b>为什么酷狗的 playTemplate 默认留空</b>：酷狗的可行路径（本机 HTTP 桥接、CDP 通道）
/// 都需要先在酷狗侧预热，没有一条能确证为稳定的公开 scheme 格式。没验证过的模板宁可不填 ——
/// 填错的后果是"放了一首别的歌，而用户以为是自己点的那首"，比放不成糟糕得多。</para>
/// </summary>
public sealed class PlatformConfig
{
    /// <summary>平台标识：netease / qq / kugou。</summary>
    public string Kind { get; set; } = "";

    /// <summary>false 时跳过这个平台（配置里预置开关再改比删条目方便）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 酷狗专用：播放用的 URL 模板，须含 <c>{id}</c>。
    ///
    /// <para>只有用户自己验证过格式时才填。酷狗的 <c>kugou://</c> 协议确实注册了
    /// （<c>KuGou.exe "%1"</c>），但公开格式没确证 —— 我们不猜，猜错会放错歌。</para>
    /// </summary>
    public string? PlayTemplate { get; set; }

    /// <summary>
    /// 是否允许用「客户端界面点歌」（驱动客户端自己的搜索框）。
    ///
    /// <para><b>为什么默认关闭</b>：这条路径会**接管用户的屏幕**——把客户端窗口拉到前台、
    /// 在搜索框里打字、点第一条结果。对点歌台来说这是想要的效果，但对正在用电脑的人来说是打扰，
    /// 所以必须显式开启。</para>
    ///
    /// <para>另外它需要针对每个客户端做过坐标标定（见 <c>descriptors/*.json</c>），
    /// 没标定的平台即使开了也会如实报"未标定"，不会瞎点。</para>
    /// </summary>
    public bool ClientUiPick { get; set; }
}

/// <summary>顶层配置。</summary>
public sealed class TakeoverConfig
{
    /// <summary>用户是否已知悉非官方接口风险并同意启用。默认 false。</summary>
    public bool AcknowledgeRisk { get; set; }

    public List<PlatformConfig> Platforms { get; set; } = new();
}

/// <summary>把配置文件变成一串可用的平台。</summary>
public static class PlatformConfigLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 读取配置并构造平台列表。**任何配置问题都不抛异常**，只记日志并返回空列表 ——
    /// 配置写坏了不该让点歌台起不来，退化成"只有本地曲库可搜"即可。
    /// </summary>
    /// <param name="configPath">配置文件路径。</param>
    /// <param name="pickerFactory">
    /// 按平台 kind 造「客户端界面点歌」执行器；返回 null 表示这个平台没标定/没启用。
    /// </param>
    /// <param name="probeFactory">
    /// 按平台 kind 造播放校验探针（读系统媒体会话）；返回 null 表示没法校验。
    /// </param>
    public static IReadOnlyList<IMusicPlatform> Load(
        string configPath,
        Func<string, IMusicPicker?>? pickerFactory = null,
        Func<string, IPlaybackProbe?>? probeFactory = null,
        Func<string, IPlaybackControl?>? controlFactory = null)
    {
        try
        {
            if (!File.Exists(configPath)) return Array.Empty<IMusicPlatform>();

            var cfg = JsonSerializer.Deserialize<TakeoverConfig>(File.ReadAllText(configPath), Json);
            if (cfg is null) return Array.Empty<IMusicPlatform>();

            if (!cfg.AcknowledgeRisk)
            {
                Console.Error.WriteLine(
                    "[takeover] takeover.json 里 acknowledgeRisk=false —— 在线搜索/点歌未启用。" +
                    "三家的在线接口都是非官方的，有失效与服务条款风险，需要显式勾选才会生效。");
                return Array.Empty<IMusicPlatform>();
            }

            var result = new List<IMusicPlatform>();
            foreach (var p in cfg.Platforms ?? new List<PlatformConfig>())
            {
                if (!p.Enabled) continue;

                // 平台的 kind 先归一化再往下用。
                //
                // 为什么必须在这里收口：<see cref="PlatformConfig.Kind"/> 虽然声明成非空 string，
                // 但 System.Text.Json 反序列化时会**用 JSON 里的值覆盖字段**，所以配置里写
                // "kind": null 会真的把 Kind 变成 null（`= ""` 的初始值被覆盖掉）。
                // 以前这里直接把 p.Kind 传给工厂：工厂拿 null 去拼路径会得到 ".json" 这种
                // 垃圾文件名，去找一个不存在的标定文件；switch 那边虽然写了 `?.` 兜住了 null，
                // 但工厂已经被喂了 null。所以归一化提前做一次，后面全程用局部变量。
                var kind = p.Kind?.Trim().ToLowerInvariant() ?? "";
                if (kind.Length == 0)
                {
                    Console.Error.WriteLine("[takeover] 有一条平台配置没写 kind，跳过");
                    continue;
                }

                var picker = p.ClientUiPick ? pickerFactory?.Invoke(kind) : null;
                var probe = probeFactory?.Invoke(kind);
                var control = controlFactory?.Invoke(kind);
                switch (kind)
                {
                    case "netease":
                        result.Add(new NeteasePlatform());
                        break;
                    case "netease-cdp":
                        // 走客户端自己的控制通道（需客户端带 --remote-debugging-port 启动）
                        result.Add(new NeteaseCdpPlatform());
                        break;
                    case "qq":
                        result.Add(new QqMusicPlatform
                        {
                            Picker = picker,
                            Probe = probe,
                            Control = control,
                        });
                        break;
                    case "kugou":
                        result.Add(new KugouPlatform
                        {
                            Picker = picker,
                            Probe = probe,
                            Control = control,
                            PlayTemplate = p.PlayTemplate,
                        });
                        break;
                    case "kuwo":
                        result.Add(new KuwoPlatform
                        {
                            Picker = picker,
                            Probe = probe,
                            Control = control,
                        });
                        break;
                    case "qishui":
                        result.Add(new QishuiPlatform
                        {
                            Picker = picker,
                            Probe = probe,
                            Control = control,
                        });
                        break;
                    case "":
                        // 空 kind 已经在循环开头 continue 掉了，走不到这里 —— 保留分支只为防御
                        break;
                    default:
                        Console.Error.WriteLine($"[takeover] 未知平台 '{kind}'，跳过");
                        break;
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[takeover] 读取 {configPath} 失败：{ex.Message}；接管模式将只有本地曲库可搜索");
            return Array.Empty<IMusicPlatform>();
        }
    }
}
