using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SongLink.Host.Benchmarks;

/// <summary>
/// Benchmarks 工具入口：采样指定进程的 CPU/内存并打印汇总。
/// 用法：SongLink.Host.Benchmarks [pid|进程名] [秒数]
///   未给 pid → 监控当前进程自身；默认采样 10 秒。
/// </summary>
internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int processId = Environment.ProcessId;
        if (args.Length > 0 && !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId))
        {
            Console.Error.WriteLine($"[bench] 第一个参数应为进程 PID（收到 '{args[0]}'）");
            return;
        }

        var seconds = 10;
        if (args.Length > 1 && (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds <= 0))
        {
            Console.Error.WriteLine($"[bench] 第二个参数应为正整数秒数（收到 '{args[1]}'）");
            return;
        }

        Console.WriteLine($"=== SongLink Benchmarks：监控 PID {processId}，{seconds}s ===");

        using var dashboard = new MonitorDashboard(processId);
        dashboard.StartMonitoring();

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        dashboard.StopMonitoring();

        var cpu = dashboard.GetCpuHistory();
        var mem = dashboard.GetMemoryHistory();
        var stats = dashboard.GetStatistics();

        Console.WriteLine($"采样点：{cpu.Count}");
        Console.WriteLine($"CPU  平均 {stats.AvgCpuPercent:F1}%  峰值 {stats.MaxCpuPercent:F1}%");
        Console.WriteLine($"内存 峰值 {stats.MaxMemoryMB} MB   当前 {dashboard.CurrentMemoryMB:F1} MB");

        if (cpu.Count > 1)
        {
            Console.WriteLine("CPU 曲线（每 10 个采样点 1 列）：");
            Console.WriteLine(string.Join(' ', cpu.Where((_, i) => i % 10 == 0).Select(p => $"{p.Value,5:F0}")));
        }
        if (mem.Count > 1)
        {
            Console.WriteLine("内存曲线（MB，每 10 个采样点 1 列）：");
            Console.WriteLine(string.Join(' ', mem.Where((_, i) => i % 10 == 0).Select(p => $"{p.Value,5:F0}")));
        }
    }
}