# SongLink Bluetooth Adapter - RFCOMM + BLE GATT

## 📋 项目概述

**位置：** `src/SongLink.Host.Connectivity.Bluetooth`  
**平台：** Windows (.NET 8)  
**目标：** 双通道蓝牙连接（RFCOMM 为主，BLE 为引导）

---

## 🏗️ 架构设计

### **1. 总体架构图**

```
BluetoothHostServer
├── DiscoveryService      # mDNS/蓝牙广播发现
├── PairingService        # PIN 配对 + Token 颁发
├── RfcommChannel         # RFCOMM 主数据通道
└── BleGattService        # BLE 辅助服务（仅传输 LAN 地址+PIN）
```

---

### **2. RFCOMM 实现（主通道）**

#### **A. 服务端实现**

```csharp
namespace SongLink.Host.Connectivity.Bluetooth;

/// <summary>
/// RFCOMM 服务器（架构 §6.6 / 契约 §3.4）
/// 使用InTheHand.Net.Bluetooth库
/// </summary>
public sealed class BluetoothRfcommServer : IDisposable
{
    private readonly BluetoothListener _listener;
    private readonly Guid _serviceGuid = new("12345678-1234-1234-1234-123456789abc"); // 固定 UUID
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public string Pin { get; } = GenPin();
    public bool IsListening => _listener != null && _listener.IsListening;

    public event Action<BluetoothClient>? ClientConnected;
    public event Action<string>? ClientDisconnected;

    public BluetoothRfcommServer(int port = 0)
    {
        try 
        {
            _listener = new BluetoothListener(port);
            _listener.Start();
            
            _acceptLoop = Task.Run(AcceptLoopAsync);
            Console.WriteLine($"[bluetooth] RFCOMM 监听 (GUID: {_serviceGuid:N}, PIN: {Pin})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bluetooth] RFCOMM 启动失败：{ex.Message}");
            throw;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptClientAsync(_cts.Token);
                if (client is not null)
                {
                    ClientConnected?.Invoke(client);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[bluetooth] 接收失败：{ex.Message}");
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
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
            var helloJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var hello = FrameCodec.Parse(helloJson);
            
            if (hello is not Hello)
            {
                SendError(stream, "AUTH_FAILED");
                return;
            }
            
            // 验证 PIN 或 Token
            if (!string.IsNullOrEmpty(hello.Pin))
            {
                if (!ValidatePin(hello.Pin))
                {
                    SendError(stream, "INVALID_PIN");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(hello.Token))
            {
                if (!ValidateToken(hello.Token))
                {
                    SendError(stream, "INVALID_TOKEN");
                    return;
                }
            }
            else
            {
                SendError(stream, "NO_AUTH");
                return;
            }
            
            // 认证成功 → 回发 authResult + HostHello
            var authResult = new AuthResult 
            { 
                Ref = hello.Id ?? "",
                Ok = true,
                Token = IssueToken(remoteAddr),
                HostKey = RequireFrameSig ? GenerateHostKey() : null
            };
            
            await SendFrameAsync(stream, authResult);
            await SendFrameAsync(stream, new Hello { Role = "host", Device = Environment.MachineName });
            
            Console.WriteLine($"[bluetooth] {remoteAddr} 鉴权成功，Token: {authResult.Token[..16]}...");
            
            // TODO: 后续处理数据流...
        }
        catch (IOException ex) when (ex.InnerException is ObjectDisposedException)
        {
            Console.WriteLine($"[bluetooth] {remoteAddr} 连接断开");
        }
        finally
        {
            client.Dispose();
            ClientDisconnected?.Invoke(remoteAddr);
        }
    }

    private void SendError(BluetoothStream stream, string code)
    {
        var error = new ErrorFrame { Code = code };
        _ = SendFrameAsync(stream, error);
    }

    private async Task SendFrameAsync(BluetoothStream stream, Frame frame)
    {
        var json = FrameCodec.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes, 0, bytes.Length, _cts.Token);
    }

    private bool ValidatePin(string pin) => CryptographicOperations.FixedTimeEquals(pin, Pin);
    private bool ValidateToken(string token) => _tokens.ContainsKey(token);
    
    private string IssueToken(string device)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = device;
        return token;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener?.Dispose();
    }
}
```

#### **B. 客户端实现**

```csharp
public sealed class BluetoothRfcommClient : IDisposable
{
    private readonly BluetoothClient? _client;
    private Stream? _stream;
    
    public string RemoteAddress => _client?.RemoteMachineAddress.ToString() ?? "";
    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(string hostAddress, int timeoutMs = 5000)
    {
        var adapter = BluetoothAdapter.DefaultAdapter;
        var devices = await adapter.DiscoverDevicesAsync(timeoutMs);
        
        var targetDevice = devices.FirstOrDefault(d => d.DeviceName == hostAddress);
        if (targetDevice is null) throw new InvalidOperationException("未找到目标设备");

        var device = BluetoothDevice.FromAddress(targetDevice.Address);
        _client = device.CreateRfcommClient(SERVICE_UUID);
        await _client.ConnectAsync(timeoutMs);
        
        _stream = _client.GetStream();
    }

    public async Task SendCmdAsync(string action, JsonObject? args)
    {
        var cmd = new Cmd { Action = action, Args = args };
        var json = FrameCodec.Serialize(cmd);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _stream!.WriteAsync(bytes, 0, bytes.Length);
    }

    public async IAsyncEnumerable<Frame> ReceiveFramesAsync()
    {
        var buffer = new byte[8192];
        while (_stream != null)
        {
            try
            {
                var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var frame = FrameCodec.Parse(json);
                    if (frame is not null) yield return frame;
                }
            }
            catch (IOException) { break; }
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}
```

---

### **3. BLE GATT 实现（引导服务）**

#### **A. 主机广播服务**

```csharp
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Advertisement;

namespace SongLink.Host.Connectivity.Bluetooth;

/// <summary>
/// BLE GATT 广播服务（用于首次配对引导）
/// 发送：LAN 地址 + PIN 码（带外交换）
/// </summary>
public sealed class BluetoothBleAdvertiser : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly CancellationTokenSource _cts = new();

    public string ServiceUuid => "DEADBEEF-1234-5678-9ABC-DEF012345678";
    public string Pin { get; } = GenPin();
    public string LanAddress => GetLocalIp();

    public event Action<AndroidBleDeviceInfo>? DeviceFound;

    public void StartAdvertising()
    {
        var advData = BluetoothLEAdvertisement.CreateFromUuids(new Guid(ServiceUuid));
        
        // 设置自定义数据字段（LAN 地址和 PIN）
        var manufacturerData = new BluetoothLEManufacturerData()
        {
            CompanyId = 0xFFFF,
            Data = Encoding.ASCII.GetBytes($"{LanAddress}|{Pin}")
        };
        advData.ManufacturerData.Add(manufacturerData);

        var broadcaster = BluetoothLEAdvertisingAdvertising advertiser;
        broadcaster.Advertisement = advData;
        broadcaster.Start();
        
        Console.WriteLine($"[ble] 开始广播：LAN={LanAddress} PIN={Pin}");
    }

    private static string GetLocalIp()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        return host.AddressList
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .FirstOrDefault()?.ToString() ?? "127.0.0.1";
    }

    public void StopAdvertising()
    {
        _cts.Cancel();
        _watcher?.Stop();
    }

    public void Dispose() => _cts.Dispose();
}
```

#### **B. Android 端扫描器（对接）**

```kotlin
class BluetoothScanScanner @Inject constructor(
    private val context: Context
) : Observable<BleDeviceEvent>() {
    
    private val bleManager = BleManager.from(context)
    
    fun startScanning(serviceUuid: String, listener: ScannedCallback) {
        bleManager.startScan(listOf(UUID.fromString(serviceUuid)), object : ScanCallback {
            override fun onScanResult(callbackType: Int, result: ScanResult) {
                val device = BleDevice(
                    address = result.device.address,
                    name = result.device.name ?: "Unknown",
                    rssi = result.rssi
                )
                
                // 解析制造商数据获取 PIN
                val rawData = result.scanRecord?.manufacturerSpecificData?.get(0xFF)
                val pinPair = rawData?.decodeToString()?.split("|")
                
                if (pinPair?.size == 2) {
                    listener.onDeviceFound(device, pinPair[1])
                }
            }
        })
    }
}
```

---

### **4. 双通道切换逻辑**

```csharp
public enum BluetoothChannelType
{
    WifiWebSocket = 0,    // 主通道：Wi-Fi WebSocket
    RfcommSpp = 1,        // 备用通道：RFCOMM
    BleGatt = 2           // 引导通道：BLE GATT
}

public sealed class BluetoothChannelSelector
{
    private readonly BluetoothRfcommServer _rfcommServer;
    private readonly BluetoothBleAdvertiser _bleAdvertiser;
    
    public async Task<bool> ShouldFallbackToBluetooth(string lanUrl)
    {
        // 检测 Wi-Fi 是否可用
        var connectivity = NetworkInformation.GetInternetConnectionProfile();
        
        if (connectivity is null || !connectivity.IsWlanConnectionProfile)
        {
            Console.WriteLine("[channel] Wi-Fi 不可用，准备切换蓝牙");
            
            // 启动 BLE 广播
            _bleAdvertiser.StartAdvertising();
            
            // 等待蓝牙设备发现
            var bluetoothReady = await WaitForBluetoothChannelAsync();
            
            return bluetoothReady;
        }
        
        return false;
    }
    
    private async Task<bool> WaitForBluetoothChannelAsync()
    {
        // 尝试连接已配对的 RFCOMM 设备
        try
        {
            var client = new BluetoothRfcommClient();
            await client.ConnectAsync("pre-paired-device-id");
            return true;
        }
        catch { return false; }
    }
}
```

---

## 🔧 依赖配置

### **Host 端（.NET 8）**

```xml
<ItemGroup>
  <PackageReference Include="InTheHand.Net.Personal" Version="4.0.5" />
  <PackageReference Include="WindowsSDK.BlueTooth" Version="10.0.19041.0" />
</ItemGroup>
```

### **Android 端（Kotlin）**

```gradle
dependencies {
    implementation 'androidx.bluetooth:bluetooth:1.1.0'
    implementation 'com.google.android.gms:play-services-basement:18.2.0'
    compileOnly 'androidx.core:core:1.12.0'
}
```

---

## ⚠️ 已知限制

1. **Windows 蓝牙栈差异大** - 不同驱动兼容性需实测
2. **BLE 数据传输慢** - 仅适合传少量数据（配对信息）
3. **RFCOMM 延迟较高** - 不适合高频推送（状态更新）
4. **配对需用户确认** - 首次连接必须手动确认 PIN

---

## 📝 测试建议

1. **真机测试场景：**
   - A 类：PC ↔ PC（相同 Windows 版本）
   - B 类：PC ↔ Android 手机（主流机型）
   - C 类：PC ↔ Android 平板

2. **验收标准：**
   - RFCOMM 稳定传输 State 帧 ≤ 100ms 延迟
   - BLE 配对成功率 ≥ 90%
   - 断线自动重连 ≤ 5 秒内完成

---

**此适配器作为 Wi-Fi WebSocket 的降级方案，非主干流程。** 🎵
