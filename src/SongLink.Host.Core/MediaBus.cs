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

    public event Action<NowPlaying>? Changed;

    public NowPlaying? Current => _current;
    public string SourceMode { get; private set; } = "PC_LOCAL";

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

    private void Emit(NowPlaying np, string mode)
    {
        var snapshot = np.Clone();
        snapshot.SourceMode = mode;
        if (_gate.ShouldEmit(snapshot, DateTimeOffset.UtcNow))
        {
            _current = snapshot;
            Changed?.Invoke(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        _cts.Dispose();
    }
}
