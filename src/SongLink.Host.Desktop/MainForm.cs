using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using SongLink.Host;
using SongLink.Host.AudioStream;

namespace SongLink.Host.Desktop;

/// <summary>
/// SongLink 点歌台桌面应用 — WinForms + WebView2 包裹器。
///
/// 启动流程：
///   1. 选端口（优先默认 51999，冲突则按 51990+ 顺序扫描）
///   2. 在同进程内跑 <see cref="HostServer"/>（FakeAdapter + NullRenderer — 桌面 app 不接手机音频流）
///   3. 用 WebView2 加载 http://localhost:{port}/?pin={PIN}
///
/// 设计选择（点歌台日常使用）：
///   - 不接 PHONE_STREAM → 无须真实音频设备，本机直接玩
///   - 关闭 = 退出（用户明确选了「始终前台窗体」）
///   - WebView2 持久化用户数据到 %LOCALAPPDATA%\SongLink.Host.Desktop\WebView2，避免污染
/// </summary>
public sealed class MainForm : Form
{
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _pinLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _adapterLabel = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _webView = new();

    private HostServer? _server;
    private int _port;

    public MainForm()
    {
        Text = "SongLink 点歌台";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1180, 760);
        BackColor = Color.FromArgb(15, 17, 21);   // 跟 web 面板背景一致

        _status.SizingGrip = false;
        _status.BackColor = Color.FromArgb(26, 29, 36);
        _status.ForeColor = Color.FromArgb(230, 230, 230);
        _status.ImageScalingSize = new Size(16, 16);
        _status.Items.Add(_pinLabel);
        _status.Items.Add(_adapterLabel);
        Controls.Add(_status);

        _webView.Dock = DockStyle.Fill;
        _webView.BackColor = Color.FromArgb(15, 17, 21);
        // 初始占位背景,等 CoreWebView2 真正起来
        _webView.Visible = false;
        Controls.Add(_webView);
        _webView.BringToFront();

        FormClosing += (_, e) =>
        {
            try { _server?.DisposeAsync().AsTask().Wait(1500); } catch { /* 退出路径不强求 */ }
        };

        Load += async (_, _) => await InitializeAsync();
    }

    /// <summary>选可用端口(优先 51999,占用则向上扫描到 52010)。失败抛。</summary>
    private static int PickPort()
    {
        for (int p = 51999; p <= 52010; p++)
        {
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                probe.Start();
                probe.Stop();
                return p;
            }
            catch
            {
                // 占用,试下一个
            }
        }
        throw new InvalidOperationException("未找到可用端口 (51999-52010 都被占用)");
    }

    /// <summary>桌面 app 启动:实例化 HostServer(进程内跑)→ 初始化 WebView2 → 导航到点歌台。</summary>
    private async Task InitializeAsync()
    {
        _port = PickPort();
        _server = new HostServer(new FakeAdapter(), port: _port, pin: null, audioRenderer: new NullRenderer());
        _server.Start(_port);

        // 服务端启动后, _server.Pin 是动态生成的 PIN（pin 缺省 = null 时自动生成）
        // 这里 await 一下让构造完全跑完即可(PIN 是 readonly field,在 ctor 同步生成)
        var pin = _server.Pin;
        _pinLabel.Text = $"  PIN: {pin}    端口: {_port}    局域网 URL: http://+:{_port}/";
        _adapterLabel.Text = $"  适配器: FakeAdapter (本地演示)    SongLink 点歌台桌面 v1.0";
        Text = $"SongLink 点歌台 — PIN {pin}";

        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SongLink.Host.Desktop", "WebView2");
        Directory.CreateDirectory(userDataDir);

        var env = await CoreWebView2Environment.CreateAsync(userDataDir);

        await _webView.EnsureCoreWebView2Async(env);
        _webView.Visible = true;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;   // 桌面端调试方便

        // 监听 WebView2 → URL 含 localhost 失败（端口被占、Host 没起来等）时给出 toast
        _webView.CoreWebView2.ProcessFailed += (_, e) =>
        {
            MessageBox.Show(
                $"WebView2 渲染进程失败:\n{e.Reason}\n{e.ProcessDescription}",
                "SongLink",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        var url = $"http://localhost:{_port}/?pin={pin}";
        _webView.CoreWebView2.Navigate(url);
    }
}
