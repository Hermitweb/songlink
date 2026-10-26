using System;

namespace SongLink.Host.AudioStream;

/// <summary>
/// 接收侧抖动缓冲（jitter buffer）：把 UDP 到达顺序的 PCM 帧，重排成
/// "按 seq 顺序、时间轴连续"再吐给渲染器。
///
/// <para>解决本仓库此前"收一帧解一帧直写渲染器"的三类真实问题：</para>
/// <list type="bullet">
/// <item><b>乱序</b>——网卡/路由乱序到达：帧先进窗口（<see cref="MaxReorderFrames"/> 深度），
/// 期望的 seq 一到位就按序吐，深度内的乱序零代价修正。</item>
/// <item><b>丢包</b>——seq 出现空洞：等 <see cref="GapTimeoutMs"/> 还没到就补受控数量的静音帧，
/// 把时间轴顶住（否则后面所有帧整体前移 → 越丢越快/加速）。</item>
/// <item><b>迟到旧帧</b>——seq 回绕距离 > 128 的帧直接丢（已过期）。</item>
/// </list>
///
/// <para>seq 是 <see cref="byte"/>（0..255 回卷），所有"新旧"判断用模 256 前向距离。</para>
/// </summary>
public sealed class JitterBuffer
{
    /// <summary>重排窗口深度：期望 seq 之后最多等多帧。</summary>
    public int MaxReorderFrames { get; }

    /// <summary>空洞等待上限（毫秒）：超过就补静音放行。</summary>
    public int GapTimeoutMs { get; }

    /// <summary>单次空洞最多补几帧静音（防停流很久后回来爆一段静音）。</summary>
    public int MaxSilencePerGap { get; }

    /// <summary>已插入的补帧计数（静音或 PLC 掩盖帧，指标，暴露给 metrics）。</summary>
    public long SilenceFrames { get; private set; }

    /// <summary>
    /// 丢帧填充工厂：缺口超时补帧时调用，入参为帧的 short 长度。
    /// null（默认）= 补死静音；设为 <c>OpusCodec.DecodeLost</c> 则走 Opus PLC
    /// （丢包掩盖，听感比一段死静音好得多）。工厂抛异常时回退静音，不破坏时间轴。
    /// </summary>
    public Func<int, short[]>? LossFrameFactory { get; set; }

    private readonly Dictionary<byte, short[]> _window = new();
    private readonly object _lock = new();
    private byte _expected;
    private long _lastFrameArrivedMs;
    private bool _anchored;

    public JitterBuffer(int maxReorderFrames = 8, int gapTimeoutMs = 80, int maxSilencePerGap = 16)
    {
        if (maxReorderFrames < 0) throw new ArgumentOutOfRangeException(nameof(maxReorderFrames));
        if (gapTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(gapTimeoutMs));
        if (maxSilencePerGap <= 0) throw new ArgumentOutOfRangeException(nameof(maxSilencePerGap));
        MaxReorderFrames = maxReorderFrames;
        GapTimeoutMs = gapTimeoutMs;
        MaxSilencePerGap = maxSilencePerGap;
    }

    /// <summary>
    /// 投入一帧（解码后的 PCM，长度须为 <paramref name="frameLen"/>）。
    /// 期望 seq 直接进"在途"位；更新帧进重排窗口；迟到旧帧丢弃。
    /// </summary>
    /// <returns>true = 帧被接受（在途或在窗口）；false = 被丢弃（太旧）。</returns>
    public bool Offer(byte seq, short[] pcm, long nowMs)
    {
        lock (_lock)
        {
            // 首个到达的帧锚定期望 seq（发送端未必从 0 起编号）
            if (!_anchored)
            {
                _expected = seq;
                _anchored = true;
                _lastFrameArrivedMs = nowMs;
                _frameLen = pcm.Length;
                _inFlight = pcm;
                return true;
            }

            var dist = ForwardDistance(seq, _expected);
            if (dist is > 128)
                return false;   // 回绕意义上"更旧"，过期帧

            if (dist == 0)
            {
                _inFlight = pcm;
            }
            else if (dist <= MaxReorderFrames)
            {
                _window[seq] = pcm;
            }
            // dist > MaxReorderFrames 的"太新"帧：进窗口但受深度限制——
            // 超过深度的新帧丢弃（等补静音追上）
            else if (_window.Count < MaxReorderFrames)
            {
                _window[seq] = pcm;
            }
            else
            {
                return false;   // 窗口满且是新帧：丢，避免无界积压
            }

            _lastFrameArrivedMs = nowMs;
            _frameLen = pcm.Length;
            return true;
        }
    }

    private short[] _inFlight = Array.Empty<short>();

    /// <summary>最近一次收到的 PCM 帧长度（用于合成等长静音帧）。0 = 还没收到过。</summary>
    private int _frameLen;

    /// <summary>
    /// 取下一帧。三种结果：
    /// <list type="number">
    /// <item>期望 seq 已有（在途/窗口）→ 原样返回，<c>WasSilence=false</c>；</item>
    /// <item>缺口存在但未到 <see cref="GapTimeoutMs"/> → <c>HasFrame=false</c>（调用方稍后再取）；</item>
    /// <item>缺口超时 → 补 ≤ <see cref="MaxSilencePerGap"/> 静音帧（长度=最近帧长），推进 <c>_expected</c>，
    /// <c>WasSilence=true</c>。</item>
    /// </list>
    /// 还没有任何帧到达时返回 <c>HasFrame=false</c>。
    /// </summary>
    public (bool HasFrame, short[] Pcm, bool WasSilence) TryTake(long nowMs)
    {
        lock (_lock)
        {
            if (_inFlight.Length > 0)
            {
                var pcm = _inFlight;
                _inFlight = Array.Empty<short>();
                _expected = (byte)(_expected + 1);
                return (true, pcm, false);
            }

            if (_window.TryGetValue(_expected, out var hit))
            {
                _window.Remove(_expected);
                _expected = (byte)(_expected + 1);
                return (true, hit, false);
            }

            // 有缺口：窗口里最早一帧距期望多远？
            var gap = GapDistance();
            if (gap > 0 && nowMs - _lastFrameArrivedMs < GapTimeoutMs && gap <= MaxReorderFrames)
                return (false, Array.Empty<short>(), false);   // 浅缺口，再等一拍

            if (gap > 0)
            {
                // 深缺口或已超时：补帧推进（走 PLC 工厂或死静音；长度 = 最近帧长）
                var fill = Math.Min(gap, MaxSilencePerGap);
                SilenceFrames += fill;
                for (var i = 0; i < fill; i++)
                    _expected = (byte)(_expected + 1);
                short[] lost;
                if (LossFrameFactory is { } factory)
                {
                    try { lost = factory(_frameLen); }
                    catch { lost = new short[_frameLen]; }   // PLC 失败回退静音
                }
                else
                {
                    lost = new short[_frameLen];
                }
                // 若补完还差着缺口里的最早帧，把它留在窗口，下一次循环再接
                return (true, lost, true);
            }

            // 完全空窗（流停了）：不补、不推进，等下一包
            return (false, Array.Empty<short>(), false);
        }
    }

    /// <summary>窗口里最早一帧距期望 seq 的前向距离；空窗口返回 0。</summary>
    private int GapDistance()
    {
        if (_window.Count == 0) return 0;
        byte oldest = default;
        int best = int.MaxValue;
        bool found = false;
        foreach (var k in _window.Keys)
        {
            var d = ForwardDistance(k, _expected);
            if (d is > 0 and < 128 && d < best) { best = d; oldest = k; found = true; }
        }
        return found ? best : 0;
    }

    /// <summary>模 256 前向距离：(a-b) mod 256，1..127 = a 比 b 新 1..127 帧。</summary>
    public static int ForwardDistance(byte a, byte b) => (byte)(a - b);

    /// <summary>Stop→Start 重拉流时复位（seq 重新从 0 起）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _window.Clear();
            _inFlight = Array.Empty<short>();
            _expected = 0;
            _lastFrameArrivedMs = 0;
            _anchored = false;
            SilenceFrames = 0;
        }
    }
}
