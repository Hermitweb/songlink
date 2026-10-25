using SongLink.Host;
using SongLink.Host.Adapters.LocalSource;
using SongLink.Host.Adapters.Smtc;
using SongLink.Host.Adapters.UiaPick;
namespace SongLink.Host.Desktop;

/// <summary>桌面 app 可选的音源。</summary>
public enum DesktopSource
{
    /// <summary>本地音乐文件夹：搜索/点歌/播放/音量全能力，不需要任何校准。</summary>
    LocalMusic,

    /// <summary>接管电脑上正在播放的 App（SMTC）：播放/暂停/上下首 + 读元数据，不能搜索点歌。</summary>
    Takeover,
}

/// <summary>
/// 音源装配 + 偏好持久化。
///
/// 之前桌面 app 直接 <c>new FakeAdapter()</c>，于是安装包里的产品只能播演示假数据
/// （固定的几首 demo 歌 + 编出来的歌词 + 占位封面），**根本没法真正控制电脑上的音乐软件**。
/// 这里把两个"即开即用、不需校准"的适配器接上，让装完就能用。
/// </summary>
public static class DesktopSources
{
    /// <summary>
    /// 数据根目录：曲库(library)与封面(art)都放这里。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>AppContext.BaseDirectory</c>：单文件发布时 BaseDirectory 指向每次启动
    /// 重新解压的临时目录，放那里等于用户的歌每次重启都"丢失"。
    /// 放在 %LOCALAPPDATA%\SongLink 下，与安装包默认目录一致且稳定。
    ///
    /// **这个目录必须同时传给适配器和 HostServer**：适配器从它搜索/播放，
    /// HostServer 从它写入「导入本地音乐」的结果，两者不一致的话
    /// 用户导进去的歌会一个都搜不到。
    /// </remarks>
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SongLink");

    /// <summary>本地音乐库目录。</summary>
    public static string LibraryDirectory => Path.Combine(DataRoot, "library");

    private static string PreferenceFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SongLink", "source.txt");

    public static string Describe(DesktopSource source) => source switch
    {
        DesktopSource.LocalMusic => "本地音乐",
        DesktopSource.Takeover => "接管正在播放",
        _ => "演示数据",
    };

    /// <summary>该音源支持的能力提示，直接显示在状态栏，避免用户点了没反应。</summary>
    public static string CapabilityHint(DesktopSource source) => source switch
    {
        DesktopSource.LocalMusic => "可搜索点歌/音量",
        // 接管模式现在能搜（本地曲库），但点歌要看本机是否配了深链接策略 —— 配了就真能点。
        DesktopSource.Takeover => "可搜索·点歌需深链接",
        _ => "演示数据，不可用于真实点歌",
    };

    public static IPlayerAdapter Create(DesktopSource source)
    {
        try
        {
            return source switch
            {
                DesktopSource.LocalMusic => new LocalSourceAdapter(new LocalLibrary(LibraryDirectory)),
                // 接管模式：传输走 SMTC（对任意在播 App 都有效），搜索聚合本地曲库 +
                // 配置里启用的在线平台（网易云/QQ音乐/酷狗），点歌按结果的来源路由。
                DesktopSource.Takeover => new TakeoverAdapter(
                    transport: new SmtcAdapter(),
                    catalogue: new LocalSourceAdapter(new LocalLibrary(LibraryDirectory)),
                    platforms: OnlinePlatforms()),
                _ => new FakeAdapter(),
            };
        }
        catch (Exception ex)
        {
            // 适配器初始化失败不能让整个 app 起不来 —— 退回演示数据并在状态栏说明。
            Console.Error.WriteLine($"[adapter] 创建 {Describe(source)} 适配器失败：{ex.Message}，退回演示数据");
            return new FakeAdapter();
        }
    }

    /// <summary>
    /// 接管模式启用的在线平台 —— 全部来自配置，**默认一个都没有**。
    ///
    /// 配置在 <c>%LOCALAPPDATA%\SongLink\takeover.json</c>，示例见
    /// <c>docs/takeover.example.json</c>。为什么不内置：
    /// 三家的在线搜索与点歌都依赖非官方接口/私有协议，有失效与服务条款风险；
    /// 且各 App 在 Windows 上支持度差异很大（实测：网易云走 CDP 控制通道；
    /// QQ音乐/酷狗只能靠客户端界面点歌，且需要坐标标定）。
    /// <see cref="DeepLink.TakeoverConfig.AcknowledgeRisk"/> 为 false 时即使写了配置也不生效 ——
    /// 点歌失败会如实报错，而不是假装点成。
    /// </summary>
    private static IEnumerable<DeepLink.IMusicPlatform> OnlinePlatforms()
        => DeepLink.PlatformConfigLoader.Load(
            TakeoverConfigFile,
            pickerFactory: CreatePicker,
            probeFactory: CreateProbe,
            controlFactory: CreateControl);

    /// <summary>
    /// 造「客户端界面点歌」执行器：按平台的标定数据（descriptors/*.json）建。
    /// 没有标定数据、或标定里没填坐标 → 返回 null（该平台就只有搜索能力）。
    /// </summary>
    private static DeepLink.IMusicPicker? CreatePicker(string kind)
    {
        var desc = LoadDescriptor(kind);
        if (desc is null) return null;
        if (desc.SearchBoxRatio is not { Length: 2 }) return null;
        return new CoordinateMusicPicker(desc);
    }

    /// <summary>
    /// 造播放校验探针：绑定到该客户端自己的系统媒体会话
    /// （QQ音乐 = QQMusic.exe，酷狗 = kugou），这样校验"点的那首有没有响"时
    /// 不会问到别的 App 的会话上去。
    /// </summary>
    private static DeepLink.IPlaybackProbe? CreateProbe(string kind) => SessionAdapter(kind);

    /// <summary>造传输控制（切歌后仍是暂停时补一次"播放"）。</summary>
    private static DeepLink.IPlaybackControl? CreateControl(string kind) => SessionAdapter(kind);

    /// <summary>
    /// 每个平台一个 SMTC 适配器实例（按客户端的媒体会话 AppId 定向）。
    /// 探针与传输控制必须共用同一个实例 —— 它们得指向同一个客户端的会话。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SmtcAdapter> _sessions = new();

    private static SmtcAdapter? SessionAdapter(string kind) => _sessions.GetOrAdd(kind, k =>
    {
        var desc = LoadDescriptor(k);
        var appId = desc?.MediaSessionAppId;
        return string.IsNullOrWhiteSpace(appId) ? null! : new SmtcAdapter { TargetAppId = appId };
    });

    /// <summary>读取某个平台的标定数据。</summary>
    private static AppDescriptor? LoadDescriptor(string kind)
    {
        foreach (var name in new[] { kind, kind + "-final" })
        {
            foreach (var dir in DescriptorDirs())
            {
                var path = Path.Combine(dir, name + ".json");
                if (!File.Exists(path)) continue;
                try { return AppDescriptor.LoadFile(path); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[takeover] 读取标定数据 {path} 失败：{ex.Message}");
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> DescriptorDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "descriptors");
        var asmDir = Path.GetDirectoryName(typeof(AppDescriptor).Assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
            yield return Path.Combine(asmDir, "descriptors");
    }

    /// <summary>接管模式在线搜索/点歌的配置文件路径。</summary>
    public static string TakeoverConfigFile => Path.Combine(DataRoot, "takeover.json");


    public static DesktopSource LoadPreference()
    {
        try
        {
            if (File.Exists(PreferenceFile))
            {
                var raw = File.ReadAllText(PreferenceFile).Trim();
                if (Enum.TryParse<DesktopSource>(raw, out var parsed)) return parsed;
            }
        }
        catch
        {
            // 偏好读不出来就用默认，不影响启动
        }
        // 默认本地音乐：它是唯一"装完即用、零校准、还能搜索点歌"的路径。
        return DesktopSource.LocalMusic;
    }

    public static void SavePreference(DesktopSource source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferenceFile)!);
            File.WriteAllText(PreferenceFile, source.ToString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[adapter] 保存音源偏好失败：{ex.Message}");
        }
    }
}
