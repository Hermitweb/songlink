using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using SongLink.Host;
using SongLink.Host.Adapters.UiaPick.Ocr;
using SongLink.Protocol;

namespace SongLink.Host.Adapters.UiaPick;

/// <summary>
/// UIA 点歌适配器（架构 §6.1 梯队 2，评审 M2）。组合式：
///   - 读元数据 / 传输控制委托给 inner（通常是 SmtcAdapter，可靠）；
///   - Pick（点任意歌）通过 UI Automation 驱动目标 App 的搜索框→回车→点第一条结果。
/// ⚠ 这是"点任意歌"的通用框架；每个 App 的 AutomationId/Name 需在真机用 Inspect.exe 校准，
///   CEF/Electron 套壳可能读不到控件而失败——失败时返回 false，Host 据此降级（不崩主链路）。
/// </summary>
public sealed class UiaPickAdapter : IPlayerAdapter, IDisposable
{
    private readonly IPlayerAdapter _inner;
    private readonly AppDescriptor _desc;
    private readonly Ocr.IOcrEngine? _ocr;

    public UiaPickAdapter(IPlayerAdapter inner, AppDescriptor descriptor) : this(inner, descriptor, null) { }

    public UiaPickAdapter(IPlayerAdapter inner, AppDescriptor descriptor, Ocr.IOcrEngine? ocr)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _desc = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _ocr = ocr;
    }

    public string AppId => _desc.AppId;
    public AdapterCapability Caps => _inner.Caps | AdapterCapability.Pick;
    /// <summary>OCR 兜底是否已注入可用（组合根接入 MediaOcrEngine/StaticOcrEngine 后为 true）。</summary>
    public bool HasOcrFallback => _ocr is { IsAvailable: true };

    // 读/传输：完全委托 inner（SMTC）
    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default) => _inner.ReadNowPlaying(ct);
    public Task<bool> Play(CancellationToken ct = default) => _inner.Play(ct);
    public Task<bool> Pause(CancellationToken ct = default) => _inner.Pause(ct);
    public Task<bool> Next(CancellationToken ct = default) => _inner.Next(ct);
    public Task<bool> Prev(CancellationToken ct = default) => _inner.Prev(ct);
    public Task<bool> Stop(CancellationToken ct = default) => _inner.Stop(ct);
    public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());

    // 音量转发到 inner（inner 决定实际能不能调）；UIA 本身没音量 API。
    public Task<double> GetVolume(CancellationToken ct = default) => _inner.GetVolume(ct);
    public Task<bool> SetVolume(double volume, CancellationToken ct = default) => _inner.SetVolume(volume, ct);

    /// <summary>点任意歌：先试 UIA（读得到控件时），失败回退 OCR（截图→识别→点坐标）。best-effort，均失败返回 false。</summary>
    public Task<bool> Pick(string title, string artist, CancellationToken ct = default)
    {
        var query = $"{title} {artist}".Trim();
        try
        {
            if (TryPickViaUia(query)) return Task.FromResult(true);
        }
        catch { /* UIA 失败 → 走 OCR 兜底 */ }

        try { return Task.FromResult(OcrPick(query)); }
        catch { return Task.FromResult(false); }
    }

    private bool TryPickViaUia(string query)
    {
        using var automation = new UIA3Automation();
        var win = FindMainWindow(automation);
        if (win is null) return false;

        var box =
            (_desc.SearchBoxAutomationId is not null ? win.FindFirstDescendant(cf => cf.ByAutomationId(_desc.SearchBoxAutomationId)) : null)
            ?? (_desc.SearchBoxName is not null ? win.FindFirstDescendant(cf => cf.ByName(_desc.SearchBoxName)) : null);
        if (box is null) return false;

        box.Focus();
        Keyboard.Type(query);
        Submit(automation, win);

        Thread.Sleep(Math.Max(800, _desc.PickTimeoutMs / 3));   // 等结果渲染（真机可换 WaitForElement）

        var result =
            (_desc.ResultItemAutomationId is not null ? win.FindFirstDescendant(cf => cf.ByAutomationId(_desc.ResultItemAutomationId)) : null)
            ?? (_desc.ResultItemNameRegex is not null ? win.FindFirstDescendant(cf => cf.ByName(_desc.ResultItemNameRegex)) : null);
        if (result is null) return false;

        if (_desc.DoubleClickResult) result.DoubleClick(); else result.Click();
        return true;
    }

    /// <summary>OCR 兜底（架构 R2）：点搜索框坐标→输入→回车→截图→OCR→匹配歌名→点其中心。</summary>
    private bool OcrPick(string query)
    {
        if (_ocr is null || !_ocr.IsAvailable) return false;
        if (_desc.SearchBoxPoint is not { Length: 2 }) return false;

        MouseInput.Click(_desc.SearchBoxPoint[0], _desc.SearchBoxPoint[1]);
        MouseInput.TypeText(query);
        MouseInput.PressEnter();
        Thread.Sleep(Math.Max(800, _desc.PickTimeoutMs / 3));

        var (bgra, w, h) = ScreenCapture.CaptureFullScreen();
        var ocr = _ocr.Recognize(bgra, w, h);
        Rect? region = _desc.ResultRegion is { Length: 4 } rr ? new Rect(rr[0], rr[1], rr[2], rr[3]) : null;
        var pt = Ocr.OcrPickStrategy.FindClickPoint(ocr, query, region);
        if (pt is null) return false;
        MouseInput.Click(pt.Value.X, pt.Value.Y, _desc.DoubleClickResult);
        return true;
    }

    private Window? FindMainWindow(UIA3Automation automation)
    {
        foreach (var exe in _desc.ExeNames)
        {
            var procs = Process.GetProcessesByName(exe);
            if (procs.Length > 0)
            {
                try
                {
                    var app = Application.Attach(procs[0]);
                    var w = app.GetMainWindow(automation);
                    if (w is not null) return w;
                }
                catch { /* 附加失败，试下一个 */ }
            }
        }
        if (!string.IsNullOrWhiteSpace(_desc.LaunchPath))
        {
            try
            {
                var launched = Application.Launch(_desc.LaunchPath!);
                return launched.GetMainWindow(automation);
            }
            catch { }
        }
        return null;
    }

    private void Submit(UIA3Automation automation, Window win)
    {
        var key = _desc.SubmitKey?.ToUpperInvariant() switch
        {
            "RETURN" or "ENTER" => VirtualKeyShort.RETURN,
            _ => VirtualKeyShort.RETURN
        };
        Keyboard.Press(key);
    }

    public void Dispose() { }
}
