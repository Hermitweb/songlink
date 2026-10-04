using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using SongLink.Host;
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

    public UiaPickAdapter(IPlayerAdapter inner, AppDescriptor descriptor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _desc = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public string AppId => _desc.AppId;
    public AdapterCapability Caps => _inner.Caps | AdapterCapability.Pick;

    // 读/传输：完全委托 inner（SMTC）
    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default) => _inner.ReadNowPlaying(ct);
    public Task<bool> Play(CancellationToken ct = default) => _inner.Play(ct);
    public Task<bool> Pause(CancellationToken ct = default) => _inner.Pause(ct);
    public Task<bool> Next(CancellationToken ct = default) => _inner.Next(ct);
    public Task<bool> Prev(CancellationToken ct = default) => _inner.Prev(ct);
    public Task<bool> Stop(CancellationToken ct = default) => _inner.Stop(ct);
    public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());

    /// <summary>点任意歌：UIA 驱动目标 App 搜索并播放第一条结果。best-effort，失败返回 false。</summary>
    public Task<bool> Pick(string title, string artist, CancellationToken ct = default)
    {
        try
        {
            using var automation = new UIA3Automation();
            var win = FindMainWindow(automation);
            if (win is null) return Task.FromResult(false);

            var box =
                (_desc.SearchBoxAutomationId is not null ? win.FindFirstDescendant(cf => cf.ByAutomationId(_desc.SearchBoxAutomationId)) : null)
                ?? (_desc.SearchBoxName is not null ? win.FindFirstDescendant(cf => cf.ByName(_desc.SearchBoxName)) : null);
            if (box is null) return Task.FromResult(false);

            box.Focus();
            Keyboard.Type($"{title} {artist}".Trim());
            Submit(automation, win);

            Thread.Sleep(Math.Max(800, _desc.PickTimeoutMs / 3));   // 等结果渲染（真机可换 WaitForElement）

            var result =
                (_desc.ResultItemAutomationId is not null ? win.FindFirstDescendant(cf => cf.ByAutomationId(_desc.ResultItemAutomationId)) : null)
                ?? (_desc.ResultItemNameRegex is not null ? win.FindFirstDescendant(cf => cf.ByName(_desc.ResultItemNameRegex)) : null);
            if (result is null) return Task.FromResult(false);

            if (_desc.DoubleClickResult) result.DoubleClick(); else result.Click();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);   // UIA 失败：交由上层降级为"仅传输控制/本地音源"
        }
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
