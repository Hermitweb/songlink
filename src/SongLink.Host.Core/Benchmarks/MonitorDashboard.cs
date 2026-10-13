using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SongLink.Host.Benchmarks;

/// <summary>
/// 实时监控仪表盘 - CPU/内存 (M2.5 P2-C)
///
/// 生命周期 API（IsMonitoring / StartMonitoring / StopMonitoring / GetStatistics）与同程序集的
/// NetworkLatencyAnalyzer 保持一致，构造后不自动开始采样，由调用方显式 StartMonitoring()。
/// </summary>
public sealed class MonitorDashboard : IDisposable
{
    private readonly Process _process;
    private readonly List<MetricPoint> _cpuHistory = new();
    private readonly List<MetricPoint> _memoryHistory = new();
    private readonly int _maxHistoryPoints = 300; // 5 分钟 @1s
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>是否正在采样。</summary>
    public bool IsMonitoring { get; private set; }

    /// <summary>当前 CPU 占用百分比（0~100）。</summary>
    public double CurrentCpuPercent { get; private set; }

    /// <summary>
    /// 当前工作集内存（MB）。
    /// 用 double 而非 long：WorkingSet64/(1024*1024) 本身就是除法结果，截断成 long 会丢小数部分；
    /// 且同程序集的 MetricPoint.Value、CurrentCpuPercent 都是 double，保持一致可免掉隐式转换。
    /// 需要整数展示时用 MetricStats.MaxMemoryMB（已转 long）。
    /// </summary>
    public double CurrentMemoryMB { get; private set; }

    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastSampleTime = DateTime.MinValue;
    private bool _disposed;

    /// <summary>获取历史数据</summary>
    public IReadOnlyList<MetricPoint> GetCpuHistory()
    {
        lock (_lock) return _cpuHistory.ToList().AsReadOnly();
    }

    public IReadOnlyList<MetricPoint> GetMemoryHistory()
    {
        lock (_lock) return _memoryHistory.ToList().AsReadOnly();
    }

    public MonitorDashboard(int processId)
    {
        try
        {
            _process = Process.GetProcessById(processId);
        }
        catch
        {
            _process = Process.GetCurrentProcess();
        }
    }

    /// <summary>开始采样；先同步采一帧，保证调用后历史立即非空。</summary>
    public void StartMonitoring()
    {
        if (_disposed || IsMonitoring) return;

        IsMonitoring = true;
        UpdateMetrics();

        var token = _cts.Token;
        _ = Task.Run(() => MonitorLoopAsync(token));
    }

    /// <summary>停止采样（可重复调用）。</summary>
    public void StopMonitoring()
    {
        IsMonitoring = false;
        try
        {
            if (!_cts.IsCancellationRequested) _cts.Cancel();
        }
        catch (ObjectDisposedException) { /* 已 Dispose，忽略 */ }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && IsMonitoring)
        {
            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) { break; }

            UpdateMetrics();
        }
    }

    private void UpdateMetrics()
    {
        TimeSpan cpuTime;
        try
        {
            _process.Refresh();
            cpuTime = _process.TotalProcessorTime;
        }
        catch
        {
            return; // 目标进程已退出：保留最后一次采样，不再追加
        }

        var now = DateTime.UtcNow;
        if (_lastSampleTime != DateTime.MinValue)
        {
            var elapsedReal = (now - _lastSampleTime).TotalMilliseconds;
            var elapsedCpu = (cpuTime - _lastCpuTime).TotalMilliseconds;
            if (elapsedReal > 0)
            {
                // TotalProcessorTime 是所有逻辑核的累加值，要按核数归一才是「占用百分比」
                var cores = Math.Max(Environment.ProcessorCount, 1);
                CurrentCpuPercent = Math.Min((elapsedCpu / elapsedReal) * 100.0 / cores, 100);
            }
        }
        _lastCpuTime = cpuTime;
        _lastSampleTime = now;

        // Memory: WorkingSet64 in MB
        CurrentMemoryMB = _process.WorkingSet64 / (1024.0 * 1024.0);

        lock (_lock)
        {
            _cpuHistory.Add(new MetricPoint(now, CurrentCpuPercent));
            _memoryHistory.Add(new MetricPoint(now, CurrentMemoryMB));

            // 限制历史长度
            if (_cpuHistory.Count > _maxHistoryPoints) _cpuHistory.RemoveAt(0);
            if (_memoryHistory.Count > _maxHistoryPoints) _memoryHistory.RemoveAt(0);
        }
    }

    /// <summary>汇总统计（无采样时返回全 0）。</summary>
    public MetricStats GetStatistics()
    {
        lock (_lock)
        {
            var cpu = _cpuHistory.Select(p => p.Value).ToArray();
            var mem = _memoryHistory.Select(p => p.Value).ToArray();

            return new MetricStats
            {
                AvgCpuPercent = cpu.Length > 0 ? cpu.Average() : 0,
                MaxCpuPercent = cpu.Length > 0 ? cpu.Max() : 0,
                MaxMemoryMB = mem.Length > 0 ? (long)mem.Max() : 0
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();
        _cts.Dispose();
        _process.Dispose();
    }
}

public sealed class MetricPoint
{
    public DateTime Timestamp { get; }
    public double Value { get; }

    public MetricPoint(DateTime timestamp, double value)
    {
        Timestamp = timestamp;
        Value = value;
    }
}

/// <summary>CPU/内存汇总（与 NetworkStats 对称）。</summary>
public sealed class MetricStats
{
    public double AvgCpuPercent { get; set; }
    public double MaxCpuPercent { get; set; }
    public long MaxMemoryMB { get; set; }
}