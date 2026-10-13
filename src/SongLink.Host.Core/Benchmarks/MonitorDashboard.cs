using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SongLink.Host.Benchmarks;

/// <summary>
/// 实时监控仪表盘 - CPU/内存 (M2.5 P2-C)
/// </summary>
public sealed class MonitorDashboard : IDisposable
{
    private readonly Process _process;
    private readonly List<MetricPoint> _cpuHistory = new();
    private readonly List<MetricPoint> _memoryHistory = new();
    private readonly int _maxHistoryPoints = 300; // 5 分钟 @1s
    
    public double CurrentCpuPercent { get; private set; }
    public long CurrentMemoryMB { get; private set; }
    
    /// <summary>获取历史数据</summary>
    public IReadOnlyList<MetricPoint> GetCpuHistory() => _cpuHistory.AsReadOnly();
    public IReadOnlyList<MetricPoint> GetMemoryHistory() => _memoryHistory.AsReadOnly();
    
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
        
        StartMonitoring();
    }
    
    private async Task StartMonitoring()
    {
        while (!_disposed)
        {
            UpdateMetrics();
            await Task.Delay(1000);
        }
    }
    
    private void UpdateMetrics()
    {
        using var proc = Process.GetCurrentProcess();
        
        // CPU: TotalProcessorTime / elapsed real time
        var totalProcessorTime = proc.TotalProcessorTime.TotalMilliseconds;
        if (_lastUpdate > TimeSpan.Zero)
        {
            var elapsedRealTime = DateTime.Now - _lastUpdateTime;
            if (elapsedRealTime > TimeSpan.Zero)
            {
                CurrentCpuPercent = Math.Min((totalProcessorTime / elapsedRealTime.TotalMilliseconds) * 10, 100);
            }
        }
        
        // Memory: WorkingSet64 in MB
        CurrentMemoryMB = proc.WorkingSet64 / (1024.0 * 1024.0);
        
        // 添加历史记录
        _cpuHistory.Add(new MetricPoint(DateTime.Now, CurrentCpuPercent));
        _memoryHistory.Add(new MetricPoint(DateTime.Now, CurrentMemoryMB));
        
        // 限制历史长度
        if (_cpuHistory.Count > _maxHistoryPoints) _cpuHistory.RemoveAt(0);
        if (_memoryHistory.Count > _maxHistoryPoints) _memoryHistory.RemoveAt(0);
        
        _lastUpdateTime = DateTime.Now;
    }
    
    private bool _disposed;
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private TimeSpan _lastUpdate = TimeSpan.Zero;
    
    public void Dispose() => _disposed = true;
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
