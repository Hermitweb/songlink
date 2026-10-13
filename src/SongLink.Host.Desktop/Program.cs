using System.Threading;

namespace SongLink.Host.Desktop;

internal static class Program
{
    private const string MutexName = "SongLink.Host.Desktop.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已有实例在跑 — 提示后退出
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
            // 启动失败弹窗(常见: WebView2 runtime 缺失)
            System.Windows.Forms.MessageBox.Show(
                $"SongLink 启动失败:\n{ex.Message}",
                "SongLink",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }
}
