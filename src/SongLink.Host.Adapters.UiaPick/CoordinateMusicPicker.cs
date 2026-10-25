using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SongLink.Host;
using SongLink.Host.DeepLink;

namespace SongLink.Host.Adapters.UiaPick;

/// <summary>
/// 坐标驱动的"在音乐客户端里点歌"执行器。
///
/// <para><b>为什么需要它</b>：QQ音乐/酷狗的 Windows 桌面客户端都没有可用的注入通道
/// （实测：QQ音乐无 CDP、7 种命令行参数全部无效、二进制里没有 <c>playbysongid</c>；
/// 酷狗远程调试被编译版禁用，社区那套 workbuddy 桥要客户端 ≥ 20.1.40）。
/// 剩下唯一通用、跨版本的办法，就是驱动客户端**自己的搜索框**：聚焦 → 搜歌名 → 回车 → 点第一条结果。</para>
///
/// <para><b>为什么坐标是"窗口相对 + 归一化"</b>：绝对屏幕坐标换台电脑、换个分辨率、换个缩放就废了。
/// 用窗口内 0..1 的相对位置，窗口多大、多小、在哪都能对上。</para>
///
/// <para><b>为什么用剪贴板粘贴而不是逐字敲</b>：中文歌名用键盘事件打不可靠（IME 状态、
/// 字符编码、按键布局都可能出错），粘贴是确定性的。</para>
///
/// <para><b>注意</b>：这里只负责"把点击做出去"，**不负责判断有没有真的响起来** ——
/// 那是 <see cref="PlaybackVerifier"/> 的事（用系统媒体会话确认）。</para>
/// </summary>
public sealed class CoordinateMusicPicker : IMusicPicker
{
    private readonly AppDescriptor _desc;

    public CoordinateMusicPicker(AppDescriptor descriptor)
    {
        _desc = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public string AppId => _desc.AppId;

    /// <summary>注入的输入器（便于单测替换）。</summary>
    public IInputSimulator Input { get; init; } = new Win32InputSimulator();

    /// <summary>注入的窗口查找器（便于单测替换）。</summary>
    public IWindowLocator Locator { get; init; } = new Win32WindowLocator();

    public async Task<PickOutcome> PickAsync(string title, string artist, IPlaybackProbe? probe = null, CancellationToken ct = default)
    {
        if (_desc.SearchBoxRatio is not { Length: 2 })
            return new PickOutcome(
                PickStatus.Unsupported,
                $"{_desc.DisplayName} 还没有标定搜索框位置（searchBoxRatio），无法自动点歌");

        var win = Locator.Find(_desc);
        if (win is null)
            return new PickOutcome(
                PickStatus.NotRunning,
                $"{_desc.DisplayName} 没有找到可交互的主窗口（可能没启动，或窗口类名/标题对不上）");

        // 客户端可能在跑但窗口收着（缩到托盘/隐藏/最小化）—— 先把它还原出来
        if (!win.IsVisible)
        {
            Input.Restore(win.Handle);
            Thread.Sleep(400);
            win = Locator.Find(_desc) ?? win;
        }

        // 全屏歌词页（酷狗歌手模式）里没有搜索框，点下去只会乱按。
        // 只对"全屏时确实没有搜索框"的客户端生效（见 AppDescriptor.BlockWhenScreenFilling）——
        // QQ音乐 只是被最大化的话，界面照常可用。
        if (_desc.BlockWhenScreenFilling && win.IsFullScreen)
            return new PickOutcome(
                PickStatus.Failed,
                $"{_desc.DisplayName} 当前是全屏歌词页（歌手模式），请先切回主界面再点歌");

        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";

        try
        {
            // 把窗口调回标定尺寸：坐标是按这个尺寸量的，不归一就没法保证点对地方
            if (_desc.CalibrationWidth is { } cw && _desc.CalibrationHeight is { } ch)
                Input.Resize(win.Handle, cw, ch);

            if (!Input.Foreground(win.Handle))
            {
                // 前台失败（Windows 的前台锁）→ 再试一次，失败就别乱点
                Thread.Sleep(200);
                if (!Input.Foreground(win.Handle))
                    return new PickOutcome(
                        PickStatus.Failed,
                        $"{_desc.DisplayName} 窗口无法获得焦点（可能被别的窗口挡住了）");
            }
            Thread.Sleep(250);

            // **必须确认目标窗口真的在前台**，否则点击/按键会落到别的应用上
            // （面板在浏览器里、用户在用别的程序时，这一步是安全底线）
            var fg = Input.ForegroundWindow();
            if (fg != win.Handle)
                return new PickOutcome(
                    PickStatus.Failed,
                    $"{_desc.DisplayName} 没能切到前台（当前前台是别的程序），已放弃点击以免误操作");

            // 调整尺寸后窗口位置/大小可能变了，重新取一次
            win = Locator.Find(_desc) ?? win;

            var box = win.Absolute(_desc.SearchBoxRatio!);
            Input.Click(box.X, box.Y);
            Thread.Sleep(150);

            // 清空 + 粘贴 + 回车
            Input.SelectAll();
            Input.PasteText(query);
            Thread.Sleep(200);
            Input.PressEnter();
        }
        catch (Exception ex)
        {
            return new PickOutcome(PickStatus.Failed, $"{_desc.DisplayName} 点歌失败：{ex.Message}");
        }

        // 等搜索结果渲染出来（客户端本地检索，通常 1~2 秒）
        var wait = Math.Max(500, _desc.PickTimeoutMs);
        Thread.Sleep(wait);

        if (_desc.ResultItemRatio is not { Length: 2 })
        {
            return new PickOutcome(
                PickStatus.Unsupported,
                $"{_desc.DisplayName} 还没有标定搜索结果位置（resultItemRatio），无法自动点歌");
        }

        // 逐行试点：用系统媒体会话确认哪一行才是目标歌。
        // （坐标只能点到"某一行"，而客户端的列表布局会随版本/窗口大小变，
        //   赌一个固定坐标不可靠；试几行、用真实播放状态确认，可靠得多。）
        var rows = Math.Max(1, _desc.MaxResultRows);
        var rowStep = _desc.ResultRowHeightRatio is { } h ? h : 0.0;

        for (var i = 0; i < rows; i++)
        {
            try
            {
                var ratio = new[] { _desc.ResultItemRatio[0], _desc.ResultItemRatio[1] + rowStep * i };
                var item = win.Absolute(ratio);
                Input.DoubleClick(item.X, item.Y);
            }
            catch (Exception ex)
            {
                return new PickOutcome(
                    PickStatus.Failed, $"{_desc.DisplayName} 点击搜索结果失败：{ex.Message}");
            }

            // 给客户端一点时间切歌，然后问系统媒体会话"现在放的是不是目标歌"
            var hit = await WaitForTargetAsync(probe, title, artist, ct);
            if (hit) return PickOutcome.Started("已点击搜索结果");

            if (ct.IsCancellationRequested) return new PickOutcome(PickStatus.Failed, "已取消");
        }

        return new PickOutcome(
            PickStatus.WrongSong,
            $"试了 {rows} 行都没点到《{title}》（客户端的搜索结果排序与预期不一致）");
    }

    /// <summary>问探针：现在放的是不是目标歌（不要求正在播，只看是不是那首）。</summary>
    private static async Task<bool> WaitForTargetAsync(
        IPlaybackProbe? probe, string title, string artist, CancellationToken ct)
    {
        if (probe is null) return false;   // 没有探针就只能赌第一个坐标
        var deadline = DateTime.UtcNow.AddMilliseconds(2500);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snap = await probe.ReadAsync(ct);
                if (snap is not null && PlaybackVerifier.Matches(snap, title, artist)) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            await Task.Delay(350, ct);
        }
        return false;
    }
}

/// <summary>屏幕尺寸（Win32，避免为了一个尺寸就引入 WinForms 依赖）。</summary>
internal static class NativeScreen
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    /// <summary>主屏宽度（像素）。</summary>
    internal static int Width => GetSystemMetrics(0);

    /// <summary>主屏高度（像素）。</summary>
    internal static int Height => GetSystemMetrics(1);
}

    /// <summary>
    /// 窗口句柄 + 位置。
    /// </summary>
    /// <param name="Handle">窗口句柄。</param>
    /// <param name="Left">窗口左边（屏幕坐标）。</param>
    /// <param name="Top">窗口上边（屏幕坐标）。</param>
    /// <param name="Width">窗口宽。</param>
    /// <param name="Height">窗口高。</param>
    /// <param name="Title">窗口标题。</param>
    /// <param name="ScreenWidth">屏幕宽（不给就取真实值；单测用假值）。</param>
    /// <param name="ScreenHeight">屏幕高（同上）。</param>
    /// <param name="IsVisible">窗口当前是否可见（客户端可能缩到托盘/隐藏）。</param>
public sealed record TargetWindow(
    IntPtr Handle, int Left, int Top, int Width, int Height, string Title,
    int? ScreenWidth = null, int? ScreenHeight = null, bool IsVisible = true)
{
    /// <summary>
    /// 是否"铺满"屏幕（多半是全屏歌词页/演示页，不是主界面）。
    ///
    /// <para>不用"必须等于屏幕尺寸"：全屏歌词页一般会避开任务栏，酷狗歌手模式实测是
    /// 1920x1032（屏幕 1920x1080），差一个任务栏高度。所以按比例判定更贴近实际。</para>
    /// </summary>
    public bool IsFullScreen
    {
        get
        {
            var sw = ScreenWidth ?? NativeScreen.Width;
            var sh = ScreenHeight ?? NativeScreen.Height;
            if (sw <= 0 || sh <= 0) return false;
            return Left <= 0 && Top <= 0 && Width >= sw * 0.9 && Height >= sh * 0.85;
        }
    }

    /// <summary>把窗口内归一化坐标换算成屏幕绝对坐标。</summary>
    public (int X, int Y) Absolute(double[] ratio) =>
        (Left + (int)Math.Round(ratio[0] * Width), Top + (int)Math.Round(ratio[1] * Height));
}

/// <summary>找目标窗口。</summary>
public interface IWindowLocator
{
    TargetWindow? Find(AppDescriptor desc);
}

/// <summary>模拟键盘/鼠标。</summary>
public interface IInputSimulator
{
    bool Foreground(IntPtr hwnd);

    /// <summary>当前前台窗口句柄。</summary>
    IntPtr ForegroundWindow();

    /// <summary>把隐藏/最小化的窗口还原出来。</summary>
    void Restore(IntPtr hwnd);

    /// <summary>把窗口调整到标定尺寸（坐标是按这个尺寸量的）。</summary>
    void Resize(IntPtr hwnd, int width, int height);

    void Click(int x, int y);
    void DoubleClick(int x, int y);
    void SelectAll();
    void PasteText(string text);
    void PressEnter();
}

/// <summary>Win32 实现的窗口查找。</summary>
public sealed class Win32WindowLocator : IWindowLocator
{
    public TargetWindow? Find(AppDescriptor desc)
    {
        foreach (var exe in desc.ExeNames)
        {
            var found = FindByProcess(exe, desc);
            if (found is not null) return found;
        }
        return null;
    }

    private static TargetWindow? FindByProcess(string exe, AppDescriptor desc)
    {
        var procs = Process.GetProcessesByName(exe);
        if (procs.Length == 0) return null;

        var pids = procs.Select(p => (int)p.Id).ToHashSet();

        // 先按"类名 + 标题"找；找不到再按"只看类名"找。
        // 为什么要有兜底：音乐客户端开始播放后窗口标题会变（QQ音乐 从"QQ音乐"变成
        // "晴天+安静+… - 周杰伦 / 林俊杰"），只按标题找会在"正在播放"时找不到窗口。
        return Match(pids, desc, requireTitle: true) ?? Match(pids, desc, requireTitle: false);
    }

    private static TargetWindow? Match(HashSet<int> pids, AppDescriptor desc, bool requireTitle)
    {
        TargetWindow? best = null;

        foreach (var hwnd in EnumWindows())
        {
            if (!GetWindowThreadProcessId(hwnd, out var pid) || !pids.Contains((int)pid)) continue;

            var title = GetWindowTitle(hwnd);
            var cls = GetClassName(hwnd);

            if (desc.WindowClassName is { Length: > 0 } want &&
                !string.Equals(cls, want, StringComparison.OrdinalIgnoreCase))
                continue;
            if (requireTitle && desc.WindowTitleRegex is { Length: > 0 } rx &&
                !System.Text.RegularExpressions.Regex.IsMatch(title, rx))
                continue;

            if (!GetEffectiveRect(hwnd, out var r)) continue;
            var visible = IsWindowVisible(hwnd);
            var cand = new TargetWindow(hwnd, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                title, IsVisible: visible);

            // 优先可见窗口；都不可见时取"还原后面积最大"的那个
            // （客户端缩到托盘/最小化时，主窗口的当前 rect 会被折叠成一条小条，
            //   必须用 GetWindowPlacement 的正常位置才认得出它）
            if (best is null) { best = cand; continue; }
            var better = cand.Width * cand.Height > best.Width * best.Height;
            if (cand.IsVisible && !best.IsVisible) better = true;
            if (better) best = cand;
        }
        return best;
    }

    /// <summary>
    /// 取窗口的有效矩形：最小化时用"还原后"的正常位置。
    /// </summary>
    private static bool GetEffectiveRect(IntPtr hwnd, out RECT r)
    {
        if (GetWindowRect(hwnd, out r))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp) &&
                (wp.showCmd == SW_SHOWMINIMIZED || wp.showCmd == SW_SHOWMINNOACTIVE))
            {
                var n = wp.rcNormalPosition;
                r = new RECT { Left = n.Left, Top = n.Top, Right = n.Right, Bottom = n.Bottom };
            }
            return true;
        }
        return false;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMINNOACTIVE = 7;

    private static IEnumerable<IntPtr> EnumWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    private static string GetWindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}

/// <summary>Win32 实现的输入模拟（纯 BCL，不引第三方自动化库）。</summary>
public sealed class Win32InputSimulator : IInputSimulator
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int HWND_TOP = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_RETURN = 0x0D;
    private const int VK_A = 0x41;
    private const int VK_V = 0x56;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    // 内存分配在 kernel32（不是 user32）
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, int dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    private const uint CF_UNICODETEXT = 13;
    private static uint _lastSeq;

    public bool Foreground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hwnd) return true;

        // Windows 的"前台锁"：后台进程直接 SetForegroundWindow 常被系统拒绝。
        // 标准解法是 AttachThreadInput —— 临时把本线程挂到前台线程上再切前台，
        // 成功后再摘掉。（面板跑在浏览器/后台，音乐客户端通常不在前台。）
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var myThread = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
    }

    /// <summary>当前前台窗口句柄（用来确认"目标窗口真的到前台了"）。</summary>
    public IntPtr ForegroundWindow() => GetForegroundWindow();

    public void Restore(IntPtr hwnd)
    {
        // SW_RESTORE 能把最小化窗口还原；隐藏的窗口用 SW_SHOW 带出来
        ShowWindow(hwnd, SW_RESTORE);
        ShowWindow(hwnd, SW_SHOW);
        Thread.Sleep(120);
    }

    public void Resize(IntPtr hwnd, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        GetWindowRect(hwnd, out var r);
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
        Thread.Sleep(200);
    }

    public void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(80);
    }

    public void DoubleClick(int x, int y)
    {
        Click(x, y);
        Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(80);
    }

    public void SelectAll()
    {
        keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        keybd_event(VK_A, 0, 0, IntPtr.Zero);
        keybd_event(VK_A, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(60);
    }

    public void PressEnter()
    {
        keybd_event(VK_RETURN, 0, 0, IntPtr.Zero);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(60);
    }

    public void PasteText(string text)
    {
        if (!SetClipboardUnicode(text)) throw new InvalidOperationException("写入剪贴板失败");

        keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        keybd_event(VK_V, 0, 0, IntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(80);
    }

    /// <summary>把文本写进剪贴板（Unicode）。</summary>
    private static bool SetClipboardUnicode(string text)
    {
        // 剪贴板可能被别的进程占着，重试几次
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(60); continue; }
            try
            {
                if (!EmptyClipboard()) return false;
                var bytes = Encoding.Unicode.GetBytes(text + "\0");   // UTF-16LE，带结尾 0
                var mem = GlobalAlloc(0x0042 /*GMEM_MOVEABLE|GMEM_ZEROINIT*/, bytes.Length);
                if (mem == IntPtr.Zero) return false;
                var ptr = GlobalLock(mem);
                if (ptr == IntPtr.Zero) { GlobalFree(mem); return false; }
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                GlobalUnlock(mem);
                if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
                {
                    GlobalFree(mem);
                    return false;
                }
                _lastSeq = GetClipboardSequenceNumber();
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return false;
    }
}
