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
///   1. 读上次选的音源（默认「本地音乐」），构造对应适配器
///   2. 在同进程内跑 <see cref="HostServer"/>
///   3. 用 WebView2 加载 http://localhost:{port}/?pin={PIN}
///
/// 音源（状态栏右下可随时切换，切换会重启 HostServer 并重载面板）：
///   - 本地音乐：LocalSourceAdapter，NAudio 真出声，搜索/点歌/音量全能力，零校准
///   - 接管正在播放：SmtcAdapter，控制 QQ音乐/网易云/酷狗等任意在播 App 的传输，
///     但不能搜索点歌
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
    private readonly ToolStripDropDownButton _sourceMenu = new() { Text = "  音源", AutoSize = true };
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _webView = new();
    private readonly NotifyIcon _tray = new();

    private HostServer? _server;
    private int _port;
    private DesktopSource _source = DesktopSource.LocalMusic;

    public MainForm()
    {
        Text = "SongLink 点歌台";
        // 品牌图标：任务栏按钮 / 标题栏 /  Alt+Tab 都用这一份
        Icon = BrandIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1180, 760);
        BackColor = Color.FromArgb(15, 17, 21);   // 跟 web 面板背景一致

        _status.SizingGrip = false;
        _status.BackColor = Color.FromArgb(26, 29, 36);
        _status.ForeColor = Color.FromArgb(230, 230, 230);
        _status.ImageScalingSize = new Size(16, 16);
        _status.Items.Add(_pinLabel);
        _status.Items.Add(_sourceMenu);
        BuildSourceMenu();
        Controls.Add(_status);

        _webView.Dock = DockStyle.Fill;
        _webView.BackColor = Color.FromArgb(15, 17, 21);
        // 初始占位背景,等 CoreWebView2 真正起来
        _webView.Visible = false;
        Controls.Add(_webView);
        _webView.BringToFront();

        SetupTray();

        FormClosing += (_, e) =>
        {
            // 先撤托盘图标,否则进程没退干净时通知区域会留一个"幽灵图标"
            try { _tray.Visible = false; _tray.Dispose(); } catch { /* 退出路径不强求 */ }
            try { _server?.DisposeAsync().AsTask().Wait(1500); } catch { /* 退出路径不强求 */ }
        };

        Load += async (_, _) => await InitializeAsync();
    }

    /// <summary>
    /// 系统托盘图标。左键/双击把点歌台拉回前台，右键给「显示 / 退出」。
    ///
    /// 注意：关闭窗口仍然等于退出（这是产品定下的语义），托盘只是多一个快捷入口，
    /// 没有改成"关窗最小化到托盘"，避免用户以为程序还开着却找不到窗口。
    /// </summary>
    private void SetupTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "SongLink 点歌台";
        _tray.Visible = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示点歌台", null, (_, _) => ShowPanel());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowPanel();
        };
        _tray.DoubleClick += (_, _) => ShowPanel();
    }

    /// <summary>把主窗体拉回前台（从最小化恢复 + 置顶激活）。</summary>
    private void ShowPanel()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        Activate();
        BringToFront();
    }

    /// <summary>初始化失败时,把异常写到状态栏 + 弹错误框,而不是让进程静默死掉(JIT 调试那种)。</summary>
    private void ReportInitError(string stage, Exception ex)
    {
        var msg = $"[init/{stage}] {ex.GetType().Name}: {ex.Message}";
        Console.Error.WriteLine(msg);
        _pinLabel.Text = msg;
        _sourceMenu.Text = "  请重启或检查依赖 — 堆栈见 ~/SongLink/crash.log";
        // 先写日志(可能 MessageBox 也会挂 — 比如初始化早期连 UI dispatcher 都不稳)
        Program.WriteCrashLog(ex, $"MainForm.InitializeAsync/{stage}");
        try { MessageBox.Show(msg + "\n\n完整堆栈见 ~/SongLink/crash.log", "SongLink 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { /* MessageBox 自身抛了就忽略 */ }
    }

    /// <summary>构建音源下拉菜单。两个选项都是"零校准、装完即用"的路径。</summary>
    private void BuildSourceMenu()
    {
        _sourceMenu.DropDownItems.Clear();
        foreach (var s in new[] { DesktopSource.LocalMusic, DesktopSource.Takeover })
        {
            var item = new ToolStripMenuItem($"{DesktopSources.Describe(s)} — {DesktopSources.CapabilityHint(s)}")
            {
                Checked = s == _source,
                Tag = s,
            };
            item.Click += async (_, _) => await SwitchSourceAsync((DesktopSource)item.Tag);
            _sourceMenu.DropDownItems.Add(item);
        }
        UpdateSourceMenu();
    }

    private void UpdateSourceMenu()
    {
        _sourceMenu.Text = $"  音源: {DesktopSources.Describe(_source)}  ";
        // Tag 是 object?，直接 (DesktopSource)item.Tag 会在 Tag 为 null 时抛 NullReferenceException。
        // 用模式匹配一次，既安全又不用额外维护一张 id→枚举的对照表。
        foreach (ToolStripMenuItem item in _sourceMenu.DropDownItems)
            item.Checked = item.Tag is DesktopSource tag && tag == _source;
    }

    /// <summary>
    /// 切换音源。适配器是在 HostServer 构造时注入的，所以换音源必须重启 HostServer，
    /// 再让 WebView2 重新加载面板（PIN 也会随之改变，由新 URL 带过去）。
    /// </summary>
    private async Task SwitchSourceAsync(DesktopSource source)
    {
        if (source == _source) return;
        try
        {
            _sourceMenu.Enabled = false;
            _pinLabel.Text = "  正在切换音源...";

            try { if (_server is not null) await _server.DisposeAsync(); } catch { /* 旧实例失败不阻塞切换 */ }
            _server = null;

            _source = source;
            DesktopSources.SavePreference(source);

            _port = PickPort();
            _server = new HostServer(DesktopSources.Create(source), port: _port, pin: null, audioRenderer: new NullRenderer(), dataDir: DesktopSources.DataRoot);
            _server.Start(_port);

            var pin = _server.Pin;
            _pinLabel.Text = $"  PIN: {pin}    端口: {_port}    局域网 URL: http://+:{_port}/";
            Text = $"SongLink 点歌台 — PIN {pin}";
            UpdateSourceMenu();

            if (_webView.CoreWebView2 is not null)
                _webView.CoreWebView2.Navigate($"http://localhost:{_port}/?pin={pin}");
        }
        catch (Exception ex)
        {
            ReportInitError("switch-source", ex);
        }
        finally
        {
            _sourceMenu.Enabled = true;
        }
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
            _source = DesktopSources.LoadPreference();
            _port = PickPort();
            _server = new HostServer(DesktopSources.Create(_source), port: _port, pin: null, audioRenderer: new NullRenderer(), dataDir: DesktopSources.DataRoot);
            _server.Start(_port);
        }
        catch (Exception ex)
        {
            ReportInitError("host", ex);
            return;
        }

        var pin = _server.Pin;
        _pinLabel.Text = $"  PIN: {pin}    端口: {_port}    局域网 URL: http://+:{_port}/";
        UpdateSourceMenu();
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
