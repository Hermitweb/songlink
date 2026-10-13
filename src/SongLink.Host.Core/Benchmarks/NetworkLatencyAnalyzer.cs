using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace SongLink.Host.Benchmarks;

/// <summary>
/// 网络延迟抖动分析器 (M2.5 P2-C)
/// </summary>
public sealed class NetworkLatencyAnalyzer : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentQueue<LatencyMetric> _latencyHistory = new();
    private readonly CancellationTokenSource _cts = new();
    
    public bool IsMonitoring { get; private set; }
    
    /// <summary>开始监控</summary>
    public void StartMonitoring(string url, int intervalMs = 1000)
    {
        if (IsMonitoring) return;
        
        IsMonitoring = true;
        _ = Task.Run(() => MonitorLoopAsync(url, intervalMs));
    }
    
    /// <summary>停止监控</summary>
    public void StopMonitoring()
    {
        IsMonitoring = false;
        _cts.Cancel();
    }
    
    private async Task MonitorLoopAsync(string url, int intervalMs)
    {
        while (!_cts.Token.IsCancellationRequested && IsMonitoring)
        {
            try
            {
                await MeasureLatencyAsync(url);
            }
            catch (OperationCanceledException) { break; }
            
            await Task.Delay(intervalMs, _cts.Token);
        }
    }
    
    private async Task MeasureLatencyAsync(string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _httpClient.GetAsync(url, _cts.Token);
        }
        catch { /* 忽略错误 */ }
        finally
        {
            sw.Stop();
            var latencyMs = sw.ElapsedMilliseconds;
            
            _latencyHistory.Enqueue(new LatencyMetric(DateTime.Now, latencyMs));
            
            // 限制历史记录
            if (_latencyHistory.Count > 600) // 10 分钟 @10s
            {
                _latencyHistory.TryDequeue(out _);
            }
        }
    }
    
    /// <summary>获取统计信息</summary>
    public NetworkStats GetStatistics()
    {
        if (_latencyHistory.IsEmpty) return new NetworkStats();
        
        var latencies = _latencyHistory.Select(m => m.Value).ToArray();
        
        return new NetworkStats
        {
            AvgLatencyMs = latencies.Average(),
            MinLatencyMs = latencies.Min(),
            MaxLatencyMs = latencies.Max(),
            JitterMs = CalculateJitter(latencies),
            PacketLossRate = CalculatePacketLossRate()
        };
    }
    
    private static double CalculateJitter(double[] latencies)
    {
        if (latencies.Length < 2) return 0;
        
        var differences = new double[latencies.Length - 1];
        for (int i = 0; i < latencies.Length - 1; i++)
        {
            differences[i] = Math.Abs(latencies[i + 1] - latencies[i]);
        }
        
        return differences.Average();
    }
    
    private double CalculatePacketLossRate()
    {
        // 这里简化处理，实际应该基于 HTTP 状态码
        return _latencyHistory.IsEmpty ? 0 : 0;
    }
    
    public IReadOnlyList<LatencyMetric> GetLatencyHistory() => 
        _latencyHistory.ToList().AsReadOnly();
    
    public void Dispose()
    {
        StopMonitoring();
        _cts.Dispose();
        _httpClient.Dispose();
    }
}

public sealed class NetworkStats
{
    public double AvgLatencyMs { get; set; }
    public double MinLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }
    public double JitterMs { get; set; }
    public double PacketLossRate { get; set; }
}

public sealed class LatencyMetric
{
    public DateTime Timestamp { get; }
    public double Value { get; }
    
    public LatencyMetric(DateTime timestamp, double value)
    {
        Timestamp = timestamp;
        Value = value;
    }
}
