using System;
using Xunit;
using SongLink.Host.Benchmarks;

namespace SongLink.Host.Tests.Benchmarks;

/// <summary>
/// CPU/内存监控仪表盘单元测试
/// </summary>
public class CpuMemoryMonitorTests : IDisposable
{
    private readonly MonitorDashboard _monitor;
    
    public CpuMemoryMonitorTests()
    {
        // 创建当前进程监控器
        _monitor = new MonitorDashboard(Process.GetCurrentProcess().Id);
    }
    
    [Fact]
    public void StartMonitoring_SetsIsRunningToTrue()
    {
        Assert.False(_monitor.IsMonitoring);
        
        _monitor.StartMonitoring();
        
        Assert.True(_monitor.IsMonitoring);
    }
    
    [Fact]
    public void StopMonitoring_SetsIsRunningToFalse()
    {
        _monitor.StartMonitoring();
        
        _monitor.StopMonitoring();
        
        Assert.False(_monitor.IsMonitoring);
    }
    
    [Fact]
    public void GetStatistics_ReturnsValidDataStructure()
    {
        var stats = _monitor.GetStatistics();
        
        Assert.NotNull(stats);
        Assert.IsType<MetricStats>(stats);
    }
    
    [Fact]
    public void Dispose_IsSafeToCallMultipleTimes()
    {
        _monitor.Dispose();
        
        // Should not throw exception on second call
        Assert.DoesNotThrow(() => _monitor.Dispose());
    }
    
    [Fact]
    public void HistoryContainsAtLeastOnePoint_IfStarted()
    {
        _monitor.StartMonitoring();
        
        // Give it a moment to add at least one data point
        Thread.Sleep(500);
        
        var history = _monitor.GetCpuHistory();
        
        // After starting, we should have some data points
        Assert.Greater(history.Count, 0);
        
        _monitor.StopMonitoring();
    }
    
    public void Dispose()
    {
        _monitor?.Dispose();
    }
}

public sealed class MetricStats
{
    public double AvgLatencyMs { get; set; }
    public long MaxMemoryMB { get; set; }
}
