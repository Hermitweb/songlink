using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace SongLink.Tools.UiaProbe;

/// <summary>
/// UIA 探测工具（M2 点歌校准助手）：列出顶层窗口 / dump 指定窗口的 UIA 树，
/// 帮助为 AppDescriptor 找出搜索框、结果项的 AutomationId / Name / ControlType。
///
/// 用法：
///   UiaProbe list                       列出所有顶层窗口（pid + 标题 + 类名）
///   UiaProbe dump &lt;进程名或标题&gt; [深度]   dump 匹配窗口的 UIA 树（默认深度 4）
///
/// 需交互式桌面会话（真机/开发机运行）；CI/无会话下仅保证编译。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0) { PrintUsage(); return 1; }
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();

        switch (args[0].ToLowerInvariant())
        {
            case "list": return List(desktop);
            case "dump":
                if (args.Length < 2) { Console.WriteLine("需要 进程名或标题"); return 1; }
                return Dump(desktop, args[1], args.Length > 2 && int.TryParse(args[2], out var d) ? d : 4);
            default: PrintUsage(); return 1;
        }
    }

    private static Window[] TopWindows(AutomationElement desktop)
    {
        try { return desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)).OfType<Window>().ToArray(); }
        catch { return Array.Empty<Window>(); }
    }

    private static int List(AutomationElement desktop)
    {
        Console.WriteLine("PID\t标题\t\t类名");
        foreach (var w in TopWindows(desktop))
        {
            try { Console.WriteLine($"{Pid(w)}\t{Safe(w.Title)}\t{Safe(w.ClassName)}"); }
            catch { }
        }
        return 0;
    }

    private static int Dump(AutomationElement desktop, string match, int maxDepth)
    {
        var target = TopWindows(desktop).FirstOrDefault(w =>
            Safe(w.Title).Contains(match, StringComparison.OrdinalIgnoreCase) ||
            ProcName(Pid(w)).Contains(match, StringComparison.OrdinalIgnoreCase));
        if (target is null) { Console.WriteLine($"未找到匹配窗口：{match}（先跑 list 看标题/进程）"); return 2; }

        Console.WriteLine($"# 窗口：{Safe(target.Title)} (pid {Pid(target)}, class {Safe(target.ClassName)})");
        Walk(target, 0, maxDepth);
        return 0;
    }

    private static int Pid(AutomationElement el)
    {
        try { return el.Properties.ProcessId.ValueOrDefault; } catch { return 0; }
    }

    private static void Walk(AutomationElement el, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        string indent = new string(' ', depth * 2);
        try
        {
            Console.WriteLine($"{indent}- [{el.ControlType}] Name='{Safe(el.Name)}' AutoId='{Safe(el.AutomationId)}' Class='{Safe(el.ClassName)}' Rect={el.BoundingRectangle}");
        }
        catch { Console.WriteLine($"{indent}- [?] (读取属性失败)"); }

        AutomationElement[] kids;
        try { kids = el.FindAllChildren(); } catch { return; }
        foreach (var k in kids) Walk(k, depth + 1, maxDepth);
    }

    private static string ProcName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return ""; }
    }

    private static string Safe(string? s) => (s ?? "").Replace("\t", " ").Replace("\n", " ");

    private static void PrintUsage()
    {
        Console.WriteLine("UiaProbe list");
        Console.WriteLine("UiaProbe dump <进程名或标题> [深度=4]");
    }
}
