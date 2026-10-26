// 真机端到端验证工具：直接走生产代码（坐标点歌 → 逐行试点 → SMTC 校验 → 补播放）。
//
// 用途：在自己的机器上确认"点歌"到底能不能成 —— 比如换了客户端版本、换了显示器之后，
// 跑一下就知道是坐标标定过期了，还是没有版权/要会员，还是客户端根本没响应。
//
// 用法：
//   dotnet run --project tools/SongLink.Tools.PickE2E -- 稻香 周杰伦
//   dotnet run --project tools/SongLink.Tools.PickE2E -- 晴天 周杰伦 <descriptors/qqmusic.json>
//
// 退出码 0 = 真的在放这首歌；1 = 没有（原因会打印出来）。
using System.Text;
using SongLink.Host.Adapters.Smtc;
using SongLink.Host.Adapters.UiaPick;
using SongLink.Host.DeepLink;
using SongLink.Protocol;

Console.OutputEncoding = Encoding.UTF8;

string title = args.Length > 0 ? args[0] : "晴天";
string artist = args.Length > 1 ? args[1] : "周杰伦";
string descPath = args.Length > 2
    ? args[2]
    : Path.Combine(AppContext.BaseDirectory, "descriptors", "qqmusic.json");
if (!File.Exists(descPath))
{
    // 兜底：直接用仓库里的那份
    var repo = FindRepoDescriptor("qqmusic.json");
    if (repo is null)
    {
        Console.Error.WriteLine($"找不到标定数据：{descPath}");
        return 2;
    }
    descPath = repo;
}

var desc = AppDescriptor.LoadFile(descPath);
var picker = new CoordinateMusicPicker(desc);
var session = new SmtcAdapter { TargetAppId = desc.MediaSessionAppId ?? "QQMusic.exe" };

// 按 descriptor 的 appId 选平台：坐标点歌与 SMTC 校验是共用的，
// 差的只是"搜索来源 + 播放校验等待"的平台细节。
IMusicPlatform platform = desc.AppId switch
{
    "kugou" => new KugouPlatform
    {
        Picker = picker,
        Probe = session,
        Control = session,
        VerifyTimeoutMs = 12000,
    },
    "kuwo" => new KuwoPlatform
    {
        Picker = picker,
        Probe = session,
        Control = session,
        VerifyTimeoutMs = 12000,
    },
    "qishui" => new QishuiPlatform
    {
        Picker = picker,
        Probe = session,
        Control = session,
        VerifyTimeoutMs = 12000,
    },
    _ => new QqMusicPlatform
    {
        Picker = picker,
        Probe = session,
        Control = session,
        VerifyTimeoutMs = 12000,
    },
};

var item = new SearchResultItem
{
    Title = title,
    Artist = artist,
    Source = desc.AppId,
    SourceId = "0",   // 坐标点歌只用到歌名/歌手，SourceId 不参与
};

Console.WriteLine($"描述符: {descPath}");
Console.WriteLine($"标定尺寸: {desc.CalibrationWidth}x{desc.CalibrationHeight}");
Console.WriteLine($"点歌前: {await Snap(session)}");

var ok = await platform.PlayAsync(item);
Console.WriteLine($"点歌结果: {(ok ? "成功" : "失败")}  原因: {platform.LastFailureReason ?? "（无）"}");
Console.WriteLine($"点歌后: {await Snap(session)}");
return ok ? 0 : 1;

static async Task<string> Snap(IPlaybackProbe probe)
{
    var s = await probe.ReadAsync();
    return s is null ? "(无会话)" : $"{s.Title} / {s.Artist} [{s.Status}]";
}

static string? FindRepoDescriptor(string fileName)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var p = Path.Combine(dir.FullName, "src", "SongLink.Host.Adapters.UiaPick", "descriptors", fileName);
        if (File.Exists(p)) return p;
        dir = dir.Parent;
    }
    return null;
}
