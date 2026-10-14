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

    /// <summary>初始化失败时,把异常写到状态栏 + 弹错误框,而不是让进程静默死掉(JIT 调试那种)。</summary>
    private void ReportInitError(string stage, Exception ex)
    {
        var msg = $"[init/{stage}] {ex.GetType().Name}: {ex.Message}";
        Console.Error.WriteLine(msg);
        _pinLabel.Text = msg;
        _adapterLabel.Text = $"请重启或检查依赖 — 堆栈见 ~/SongLink/crash.log 或 %LOCALAPPDATA%\\SongLink\\crash.log";
        // 先写日志(可能 MessageBox 也会挂 — 比如初始化早期连 UI dispatcher 都不稳)
        Program.WriteCrashLog(ex, $"MainForm.InitializeAsync/{stage}");
        try { MessageBox.Show(msg + "\n\n完整堆栈见 ~/SongLink/crash.log", "SongLink 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { /* MessageBox 自身抛了就忽略 */ }
    }

    /// <summary>手动定位 WebView2 runtime 路径。Edge Update 注册表缺失/损坏时 CoreWebView2Environment.CreateAsync()
    /// 会抛 WebView2RuntimeNotFoundException,所以直接扫已知的安装目录并把最新的版本喂给 CreateAsync 的
    /// browserExecutableFolder 参数绕过 bootstrapper。</summary>
    private static string? TryFindWebView2Runtime()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application",
            @"C:\Program Files\Microsoft\EdgeWebView\Application",
        };
        foreach (var root in candidates)
        {
            if (!Directory.Exists(root)) continue;
            // 找最新版本号(子目录名即版本号,按字典序排序时新版大于旧版)
            var latest = Directory.GetDirectories(root)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+\.\d+\.\d+\.\d+$"))
                .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latest is null) continue;
            var full = Path.Combine(root, latest, "msedgewebview2.exe");
            if (File.Exists(full))
            {
                // CoreWebView2Environment.CreateAsync 第三个参数要求**目录**(不是 .exe 路径)
                return Path.Combine(root, latest);
            }
        }
        return null;
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

    /// <summary>桌面 app 启动:实例化 HostServer(进程内跑)→ 初始化 WebView2 → 导航到点歌台。
    /// 任一阶段失败都走 ReportInitError,而不是抛 JIT 调试对话框。</summary>
    private async Task InitializeAsync()
    {
        // 阶段 1: 起 HostServer(进程内)
        try
        {
            _port = PickPort();
            _server = new HostServer(new FakeAdapter(), port: _port, pin: null, audioRenderer: new NullRenderer());
            _server.Start(_port);
        }
        catch (Exception ex)
        {
            ReportInitError("host", ex);
            return;
        }

        var pin = _server.Pin;
        _pinLabel.Text = $"  PIN: {pin}    端口: {_port}    局域网 URL: http://+:{_port}/";
        _adapterLabel.Text = $"  适配器: FakeAdapter (本地演示)    SongLink 点歌台桌面 v1.0";
        Text = $"SongLink 点歌台 — PIN {pin}";

        // 阶段 2: WebView2 user data dir
        string userDataDir;
        try
        {
            userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SongLink.Host.Desktop", "WebView2");
            Directory.CreateDirectory(userDataDir);
        }
        catch (Exception ex)
        {
            ReportInitError("user-data-dir", ex);
            return;
        }

        // 阶段 3: 初始化 WebView2 环境(最常见的失败源:Edge runtime 找不到。
        // 这台机器 Edge Update 注册表缺失,WebView2 找不到 runtime → 显式指定 browserExecutableFolder 绕过)
        CoreWebView2Environment? env = null;
        try
        {
            string? browserFolder = TryFindWebView2Runtime();
            if (browserFolder is not null)
            {
                Console.WriteLine($"[webview2] using explicit runtime: {browserFolder}");
                var options = new CoreWebView2EnvironmentOptions();
                env = await CoreWebView2Environment.CreateAsync(browserFolder, userDataDir, options);
            }
            else
            {
                // 没找到 → 走默认(可能下载,也可能再抛 RuntimeNotFoundException 给 ReportInitError)
                env = await CoreWebView2Environment.CreateAsync(userDataDir);
            }
        }
        catch (Exception ex)
        {
            ReportInitError("webview2-env", ex);
            return;
        }

        // 阶段 4: 把 WebView2 真正 attach 上
        try
        {
            await _webView.EnsureCoreWebView2Async(env);
            _webView.Visible = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

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
        catch (Exception ex)
        {
            ReportInitError("webview2-init", ex);
        }
    }
}
