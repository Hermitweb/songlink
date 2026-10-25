using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 媒体总线（架构 §6.3 / §14.2，评审 #12）：单一"当前播放真值"仲裁者。
/// 两个输入互斥——
///   PC_LOCAL：轮询活动适配器（SMTC/本地）；
///   PHONE_STREAM：用手机上报的 srcMeta 驱动，且不再轮询适配器；进入时冻结 PC 出声（Pause）避免抢声。
/// 归一成 NowPlaying 事件流；StateGate 去抖（变化即发 / 稳态 ≤1Hz）。
/// </summary>
public sealed class MediaBus : IAsyncDisposable
{
    private readonly IPlayerAdapter _adapter;
    private readonly CancellationTokenSource _cts = new();
    private readonly StateGate _gate = new();
    private Task? _loop;
    private NowPlaying? _current;
    private double _volume = 1.0;

    public event Action<NowPlaying>? Changed;

    public NowPlaying? Current => _current;
    public string SourceMode { get; private set; } = "PC_LOCAL";

    /// <summary>当前音量（Web 面板音量条用，0.0~1.0）。</summary>
    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0.0, 1.0);
    }

    public MediaBus(IPlayerAdapter adapter) => _adapter = adapter;

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            // PHONE_STREAM 下不轮询 PC 适配器（由 srcMeta 驱动），避免两个出声源打架
            if (SourceMode == "PC_LOCAL")
            {
                try
                {
                    var np = await _adapter.ReadNowPlaying(_cts.Token);
                    if (np is not null) Emit(np, "PC_LOCAL");
                }
                catch (OperationCanceledException) { break; }
                catch { /* 适配器瞬时异常，下一拍重试 */ }
            }
            try { await Task.Delay(250, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>切换音源模式（#12）：进 PHONE_STREAM 时冻结 PC 出声；回 PC_LOCAL 时恢复轮询。</summary>
    public void SetMode(string mode)
    {
        mode = mode == "PHONE_STREAM" ? "PHONE_STREAM" : "PC_LOCAL";
        if (mode == SourceMode) return;
        SourceMode = mode;
        if (mode == "PHONE_STREAM") _ = SafePauseAsync();   // 冻结本地/被控 App，避免抢声
    }

    private async Task SafePauseAsync()
    {
        try { await _adapter.Pause(_cts.Token); } catch { /* 冻结失败不致命 */ }
    }

    /// <summary>手机上报元数据（srcMeta）→ 作为 PHONE_STREAM 真值广播。</summary>
    public void SetPhoneNowPlaying(NowPlaying np)
    {
        SourceMode = "PHONE_STREAM";
        Emit(np, "PHONE_STREAM");
    }

    /// <summary>用户/外部设置音量(0.0~1.0)：更新总线当前音量并立刻广播一次，
    /// 让 Web 面板/系统混音器同步。失败不抛（best-effort）。</summary>
    public void SetVolume(double volume, bool forceEmit = true)
    {
        var v = Math.Clamp(volume, 0.0, 1.0);
        if (Math.Abs(v - _volume) < 1e-6 && !forceEmit) return;
        _volume = v;
        if (_current is null) { _current = new NowPlaying { Volume = v }; Changed?.Invoke(_current.Clone()); return; }
        // 用现有 _current 的元数据但盖上 Volume,再 Emit,StateGate 会判定变化
        var snap = _current.Clone(); snap.Volume = v;
        Emit(snap, SourceMode, skipGate: true);   // 音量变化必须立即生效,不走 StateGate 去抖
    }

    private void Emit(NowPlaying np, string mode, bool skipGate = false)
    {
        var snapshot = np.Clone();
        snapshot.SourceMode = mode;
        // 把当前音量盖上(适配器 ReadNowPlaying 一般给 1.0;Host 才是 source of truth)
        snapshot.Volume = _volume;
        if (skipGate || _gate.ShouldEmit(snapshot, DateTimeOffset.UtcNow))
        {
            _current = snapshot;
            Changed?.Invoke(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等：重复调用 DisposeAsync 是常见写法（await using + 显式释放），
        // 第二次再 Cancel 一个已 Dispose 的 CTS 会抛 ObjectDisposedException。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* 已 dispose */ }
        if (_loop is not null) { try { await _loop; } catch { } }
        _cts.Dispose();
    }

    /// <summary>是否已经释放过（DisposeAsync 幂等保护）。</summary>
    private int _disposed;
}
