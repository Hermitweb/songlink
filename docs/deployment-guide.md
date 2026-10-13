# SongLink Host 部署指南

## 📦 系统要求

### **最低配置**

| 组件 | 要求 |
|------|------|
| **操作系统** | Windows 10 / 11（64 位） |
| **.NET 运行时** | .NET 8.0 Desktop Runtime（或自包含发布） |
| **内存** | ≥ 512MB RAM |
| **磁盘空间** | ≥ 100MB |
| **网络** | IPv4 + WebSocket 支持 |
| **蓝牙** | Windows 蓝牙栈（RFCOMM/BLE） |

---

## 🚀 快速部署

### **方案 A: 开发环境直接运行**

```powershell
cd E:\workspace\点歌台面板

# 编译并启动
dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj ^
    -- adapter=uia ^
    -- descriptor="src\SongLink.Host.Adapters.UiaPick\descriptors\qqmusic.json"
```

**预期输出：**
```
[factory] UIA 点歌适配器：inner=fake descriptor=... ocr=off
[host] 监听 ws://0.0.0.0:51999/ PIN={6 位数字} 适配器=qqmusic caps=ReadMeta,Transport,Pick,Search
按 Ctrl+C 退出。
```

---

### **方案 B: 自包含发布（生产推荐）**

#### **1. 构建发布包**

```powershell
dotnet publish src/SongLink.Host.Core/SongLink.Host.Core.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o ./publish/win-x64
```

#### **2. 验证发布产物**

```powershell
# 目录结构
.\publish\win-x64\
├── SongLink.Host.exe              # 主程序（含所有依赖）
├── SongLink.Protocol.dll          # 协议库
├── appsettings.json               # 配置文件
├── library\                       # 本地音源目录
├── lyrics\                        # LRC 歌词目录
└── art\                           # 专辑封面目录
```

#### **3. 独立运行**

```powershell
# 方式 1: 命令行启动
.\publish\win-x64\SongLink.Host.exe --adapter=uia --descriptor=appdata\qqmusic.json

# 方式 2: 环境变量指定
$env:SONGLINK_ADAPTER="uia"
.\publish\win-x64\SongLink.Host.exe
```

---

### **方案 C: Windows 服务（开机自启）**

#### **安装 NSSM 工具**

```powershell
# 下载 NSSM
Invoke-WebRequest -Uri "https://nssm.cc/release/nssm-2.24.zip" -OutFile "nssm.zip"
Expand-Archive nssm.zip -DestinationPath "nssm"

# 注册为服务
& .\nssm\nssm.exe install SongLinkHost "E:\path\to\SongLink.Host.exe"
```

#### **配置服务参数**

```powershell
& .\nssm\nssm.exe set SongLinkHost Start SERVICE_AUTO
& .\nssm\nssm.exe set SongLinkHost AppParameters "--adapter=localsource"
& .\nssm\nssm.exe set SongLinkHost AppDirectory "E:\path\to"
& .\nssm\nssm.exe set SongLinkHost ServiceDescription "SongLink Point-of-Sale Host"
```

#### **启动服务**

```powershell
Start-Service SongLinkHost
Get-Service SongLinkHost  # 查看状态
```

---

## 🔧 配置说明

### **1. 配置文件（appsettings.json）**

```json
{
  "Port": 51999,
  "Adapter": "uia",                    // fake | uia | localsource
  "Descriptor": "descriptors/qqmusic.json",
  "OcrAssembly": null,
  "OcrType": null,
  "Pin": null,                         // null → 随机生成 6 位
  "RequireFrameSig": false             // 帧签名验证
}
```

### **2. 环境变量覆盖**

```powershell
$env:SONGLINK_PORT="51998"
$env:SONGLINK_ADAPTER="localsource"
dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj
```

---

## 🔒 安全加固

### **1. 端口暴露控制**

#### **局域网可访问（默认）**
```bash
netsh advfirewall firewall add rule name="SongLink WS" dir=in protocol=TCP localport=51999 action=allow remoteip=lan
```

#### **仅本地访问（更安全）**
```powershell
# 修改 HostServer.cs Start 方法
_listenner.Prefixes.Add($"http://localhost:{port}/")
```

### **2. PIN 码强制**

```bash
dotnet run --project src/SongLink.Host.Core/SongLink.Host.Core.csproj --pin=固定 PIN
```

### **3. 帧签名模式**

启用 HMAC-SHA256 验签：
```json
{
  "RequireFrameSig": true
}
```

---

## 🐛 故障排查

### **问题 1: Port 被占用**

```bash
# 查找占用进程
netstat -ano | findstr :51999

# 结束进程
taskkill /PID <PID> /F
```

### **问题 2: Wi-Fi 不可用**

检查防火墙规则：
```powershell
netsh firewall show portexemption
netsh firewall add portopening TCP 51999 "SongLink Host" ENABLE
```

### **问题 3: Bluetooth RFCOMM 失败**

检查蓝牙服务：
```powershell
Get-Service bthserv
if ($_.Status -ne 'Running') { Start-Service bthserv }
```

---

## 📊 性能调优

### **调整 StateGate 节流频率**

修改 `StateGate.cs` constructor:
```csharp
public StateGate(TimeSpan? keepalive = null)
    => _keepalive = keepalive ?? TimeSpan.FromSeconds(1);  // 改为 0.5s 提升实时性
```

### **增加 ReplayBuffer 容量**

修改 `ReplayBuffer.cs`:
```csharp
public ReplayBuffer(int capacity = 512)  // 增加缓存帧数
```

---

## 🔄 升级策略

### **1. 平滑升级**

```bash
# 备份当前版本
cp -r /opt/songlink-host /opt/songlink-host.backup

# 替换为新版本
cp -r new-version/* /opt/songlink-host/

# 重启服务
systemctl restart songlink-host
```

### **2. 回滚方案**

```bash
# 停止服务
systemctl stop songlink-host

# 恢复旧版本
rm -rf /opt/songlink-host
cp -r /opt/songlink-host.backup /opt/songlink-host

# 启动服务
systemctl start songlink-host
```

---

## 📝 Docker 容器化（可选）

### **Dockerfile**

```dockerfile
FROM mcr.microsoft.com/dotnet/desktop-runtime:8.0

WORKDIR /app

COPY publish/win-x64/. /app/

EXPOSE 51999

ENV SONGLINK_ADAPTER=localsource

ENTRYPOINT ["./SongLink.Host.exe"]
```

### **构建镜像**

```bash
docker build -t songlink/host:latest .
```

### **运行容器**

```bash
docker run -d \
  -p 51999:51999 \
  -v /data/library:/app/library \
  -v /data/lyrics:/app/lyrics \
  --name songlink-host \
  songlink/host:latest
```

---

## ✅ 验收标准

1. **基本功能：**
   - [ ] Host 能正常启动并监听 51999 端口
   - [ ] Python Remote Simulator 能成功连接
   - [ ] 状态广播频率 ≤ 1Hz
   
2. **高级功能：**
   - [ ] FakeAdapter/UiaPickAdapter均能正常工作
   - [ ] 队列管理功能响应正确
   - [ ] 断线重连补发机制有效
   
3. **安全性：**
   - [ ] PIN 配对流程顺畅
   - [ ] Token 重连无异常
   - [ ] 未鉴权客户端无法接收广播

---

**此指南持续更新，根据实际部署环境调整。** 🎵
