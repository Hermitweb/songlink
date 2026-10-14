using System.IO;
using System.Threading;

namespace SongLink.Host.Desktop;

internal static class Program
{
    private const string MutexName = "SongLink.Host.Desktop.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // 把任何未捕获异常写到桌面 log 文件 — 自包含 exe 跑挂时好定位
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject as Exception, "UnhandledException");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            WriteCrashLog(e.Exception, "UnobservedTaskException");

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.Forms.MessageBox.Show(
                "SongLink 点歌台已在运行。",
                "SongLink",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new MainForm());
        }
        catch (System.Exception ex)
        {
            WriteCrashLog(ex, "Application.Run");
            System.Windows.Forms.MessageBox.Show(
                $"SongLink 启动失败:\n{ex.Message}",
                "SongLink",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }

    /// <summary>写崩溃日志 — 多处位置兜底(桌面可能被重定向),保证至少 stdout 有。</summary>
    public static void WriteCrashLog(Exception? ex, string source)
    {
        if (ex is null) return;
        var text = $"[{System.DateTime.Now:O}] [{source}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n\n";
        try { Console.Error.Write(text); } catch { /* stdout 关了 */ }

        // 桌面 + LocalAppData 都写,哪个有权限就用哪个
        foreach (var baseDir in new[] {
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            System.IO.Path.GetTempPath() })
        {
            try
            {
                var dir = Path.Combine(baseDir, "SongLink");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), text);
            }
            catch { /* 一个个试 */ }
        }
    }
}
