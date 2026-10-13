using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SongLink.Host.Connectivity.Bluetooth;

/// <summary>
/// Bluetooth RFCOMM 服务器（用于 Android 端配对连接）
/// GUID: 12345678-1234-1234-1234-123456789abc
///
/// 移植说明（对齐 InTheHand.Net.Bluetooth 4.2.5 的真实 API，与旧稿假设的差异）：
/// - 4.x 没有 BluetoothAdapter 这一层（Windows 上 32feet 直接走 Winsock 蓝牙栈），
///   故删掉 GetDefaultAdapter() 预检；监听器启动失败即代表本机不可用。
/// - BluetoothListener 用 ctor(Guid service) 传入服务 GUID（自动发布默认 SDP 记录），
///   没有 ServiceIdentifier 属性；就绪状态看 Active 而不是 IsListening。
/// - AcceptBluetoothClientAsync() 无 CancellationToken 重载，取消靠 WhenAny + 无限延时。
/// - BluetoothClient 没有 RemoteMachineAddress，取 RemoteMachineName / Socket.RemoteEndPoint。
/// </summary>
public sealed class RfcommServer : IDisposable
{
    /// <summary>服务 GUID；Android 端按同一 GUID 发现并连接。</summary>
    public static readonly Guid ServiceGuid = new("12345678-1234-1234-1234-123456789abc");

    private readonly BluetoothListener? listener;
    private readonly CancellationTokenSource cts = new();
    private Task? acceptLoopTask;

    public string Pin { get; } = GeneratePin();
    public bool IsListening => listener?.Active == true;

    public event Action<BluetoothClient>? ClientConnected;
    public event Action<string>? ClientDisconnected;

    public RfcommServer()
    {
        try
        {
            listener = new BluetoothListener(ServiceGuid);
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
        while (!cts.Token.IsCancellationRequested && listener?.Active == true)
        {
            Task<BluetoothClient> pending;
            try
            {
                pending = listener.AcceptBluetoothClientAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bluetooth] 接收错误：{ex.Message}");
                break;
            }

            try
            {
                // AcceptBluetoothClientAsync() 不接受 CT，用一个可取消的无限延时做竞速
                var finished = await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, cts.Token));
                if (finished != pending)
                {
                    // 走到这里说明是取消胜出：观察掉 pending 的异常，避免未处理任务异常
                    _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }

                var client = await pending;
                if (client is not null)
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
        var remoteAddr = client.RemoteMachineName;
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