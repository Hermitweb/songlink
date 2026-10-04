using System.Text.Json;
using System.Text;

namespace SongLink.Spikes.SmtcMatrix;

/// <summary>
/// 能力矩阵生成器（纯托管，无 WinRT，可跨机编译运行）
/// 输入：smtc-probe `dump` 命令产出的会话 JSON（或样例 sample-dump.json）
/// 输出：控制台对齐的能力矩阵 + smtc-capability-matrix.csv
///
/// 用法：
///   dotnet run -- [dump.json]        默认 sample-dump.json
///
/// 对应架构 §6.1（Caps）/ §10 M0 / 风险 R3（哪些 App 元数据缺失）。
/// </summary>
internal static class Program
{
    private sealed record Session(
        string source, string title, string artist, string album,
        bool hasThumbnail, string status, double positionSec, bool positionAdvancing);

    private sealed record Dump(string capturedAt, string os, List<Session> sessions);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var path = args.Length > 0 ? args[0] : "sample-dump.json";
        if (!File.Exists(path))
        {
            // 允许在 bin 目录下找不到时回退到项目目录
            var alt = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sample-dump.json");
            if (File.Exists(alt)) path = alt;
            else { Console.WriteLine($"[x] 找不到 dump 文件：{path}"); return 1; }
        }

        var dump = JsonSerializer.Deserialize<Dump>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dump?.sessions is null || dump.sessions.Count == 0)
        {
            Console.WriteLine("[!] dump 无会话数据。");
            return 0;
        }

        Console.WriteLine($"SMTC 能力矩阵 · {dump.os} · {dump.capturedAt} · 共 {dump.sessions.Count} 个会话\n");

        var headers = new[] { "App", "Title", "Artist", "Album", "封面", "进度采样", "状态" };
        var rows = new List<string[]>();
        int fullMeta = 0;
        foreach (var s in dump.sessions)
        {
            string app = ShortName(s.source);
            bool hasTitle = !string.IsNullOrWhiteSpace(s.title);
            bool hasArtist = !string.IsNullOrWhiteSpace(s.artist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(s.album);
            if (hasTitle && hasArtist && hasAlbum && s.hasThumbnail) fullMeta++;
            rows.Add(new[]
            {
                app, Mark(hasTitle), Mark(hasArtist), Mark(hasAlbum),
                Mark(s.hasThumbnail), Mark(s.positionAdvancing), s.status
            });
        }

        // 计算列宽（中文按 2 宽估算，仅用于对齐观感）
        int W(string x) => x.Sum(c => c > 0x2E80 ? 2 : 1);
        var widths = headers.Select((h, i) => Math.Max(W(h), rows.Max(r => W(r[i])))).ToArray();

        string Sep() => "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        string Line(string[] cells) => "|" + string.Join("|", cells.Select((c, i) => " " + c + new string(' ', widths[i] - W(c) + 1))) + "|";

        Console.WriteLine(Sep());
        Console.WriteLine(Line(headers));
        Console.WriteLine(Sep());
        foreach (var r in rows) Console.WriteLine(Line(r));
        Console.WriteLine(Sep());
        Console.WriteLine($"\n元数据+封面全齐的 App：{fullMeta}/{dump.sessions.Count}");
        Console.WriteLine("说明：'点任意歌'能力需 smtc-probe 的 control 主动探测（侵入式），不在本只读矩阵内。");

        // 写 CSV
        var csv = new StringBuilder();
        csv.AppendLine("app,source,title,artist,album,thumbnail,positionAdvancing,status");
        foreach (var s in dump.sessions)
            csv.AppendLine(string.Join(',', ShortName(s.source), s.source, Mark(!string.IsNullOrWhiteSpace(s.title)),
                Mark(!string.IsNullOrWhiteSpace(s.artist)), Mark(!string.IsNullOrWhiteSpace(s.album)),
                Mark(s.hasThumbnail), Mark(s.positionAdvancing), s.status));
        var outCsv = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "smtc-capability-matrix.csv");
        File.WriteAllText(outCsv, csv.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n[✓] CSV 已写出：{outCsv}");
        return 0;
    }

    private static string Mark(bool b) => b ? "✓" : "✗";

    private static string ShortName(string aumid)
    {
        // 从 AppUserModelId 里取可读产品名（启发式）
        foreach (var hint in new[] { "QQMusic", "CloudMusic", "Netease", "Kugou", "Spotify", "AppleMusic", "Kuwo" })
            if (aumid.Contains(hint, StringComparison.OrdinalIgnoreCase)) return hint;
        var bang = aumid.IndexOf('!');
        return bang > 0 ? aumid[..bang].Split('.').Last() : aumid;
    }
}
