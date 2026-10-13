usingInTheHand.Net.Sockets;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SongLink.Host.Connectivity.Bluetooth;

/// <summary>
/// Bluetooth RFCOMM 服务器（用于 Android 端配对连接）
/// GUID: 12345678-1234-1234-1234-123456789abc
/// </summary>
public sealed class RfcommServer : IDisposable
{
    private readonly Guid serviceGuid = new("12345678-1234-1234-1234-123456789abc");
    private readonly BluetoothListener? listener;
    private readonly CancellationTokenSource cts = new();
    private Task? acceptLoopTask;
    
    public string Pin { get; } = GeneratePin();
    public bool IsListening => listener?.IsListening == true;
    
    public event Action<BluetoothClient>? ClientConnected;
    public event Action<string>? ClientDisconnected;
    
    public RfcommServer(int port = 0)
    {
        try 
        {
            var adapter = BluetoothAdapter.GetDefaultAdapter();
            if (adapter == null)
            {
                Console.WriteLine("[bluetooth] 未检测到蓝牙适配器");
                return;
            }
            
            listener = new BluetoothListener(port);
            listener.ServiceIdentifier = serviceGuid;
            listener.Start();
            
            acceptLoopTask = Task.Run(AcceptLoopAsync);
            Console.WriteLine($"[bluetooth] RFCOMM 监听就绪 PIN={Pin}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bluetooth] 启动失败：{ex.Message}");
        }
    }
    
    private async Task AcceptLoopAsync()
    {
        while (!cts.Token.IsCancellationRequested && listener?.IsListening == true)
        {
            try
            {
                var client = await listener.AcceptBluetoothClientAsync(cts.Token);
                if (client != null)
                {
                    ClientConnected?.Invoke(client);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[bluetooth] 接收错误：{ex.Message}");
            }
        }
    }
    
    private async Task HandleClientAsync(BluetoothClient client)
    {
        var remoteAddr = client.RemoteMachineAddress.ToString();
        Console.WriteLine($"[bluetooth] 客户端接入：{remoteAddr}");
        
        try
        {
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            
            // 等待 Hello 帧
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            var helloJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            
            if (helloJson.Contains("\"type\":\"hello\""))
            {
                // 发送 authResult
                var authResponse = @"{""v"":1,""type"":""authResult"",""ok"":true,""token"":""" + 
                                   GenerateToken(remoteAddr) + @"""}";
                
                var responseBytes = Encoding.UTF8.GetBytes(authResponse);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cts.Token);
                
                Console.WriteLine($"[bluetooth] {remoteAddr} 鉴权成功");
            }
        }
        catch (IOException)
        {
            Console.WriteLine($"[bluetooth] {remoteAddr} 连接断开");
        }
        finally
        {
            client.Dispose();
            ClientDisconnected?.Invoke(remoteAddr);
        }
    }
    
    private static string GenerateToken(string deviceId)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
    
    private static string GeneratePin()
    {
        return Random.Shared.Next(100000, 1000000).ToString().PadLeft(6, '0');
    }
    
    public void Dispose()
    {
        cts.Cancel();
        listener?.Stop();
        listener?.Dispose();
    }
}