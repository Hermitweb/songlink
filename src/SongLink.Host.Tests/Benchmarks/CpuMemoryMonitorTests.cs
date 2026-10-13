using System;
using System.Diagnostics;
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
        // (原写成 FluentAssertions 的 Assert.DoesNotThrow；本工程用 xUnit，用 Record.Exception 表达同一断言)
        Assert.Null(Record.Exception(() => _monitor.Dispose()));
    }
    
    [Fact]
    public void HistoryContainsAtLeastOnePoint_IfStarted()
    {
        _monitor.StartMonitoring();
        
        // Give it a moment to add at least one data point
        Thread.Sleep(500);
        
        var history = _monitor.GetCpuHistory();
        
        // After starting, we should have some data points
        // (原写成 FluentAssertions 的 Assert.Greater；本工程用 xUnit)
        Assert.True(history.Count > 0, $"采样历史应有数据点，实际 {history.Count}");
        
        _monitor.StopMonitoring();
    }
    
    public void Dispose()
    {
        _monitor?.Dispose();
    }
}
