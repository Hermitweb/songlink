# SongLink User Guide 用户指南

## 🎯 产品简介

**SongLink（点歌台）** 是一套面向家庭/聚会 KTV 场景的无线点歌系统：

- **Windows Host（电脑端）：** 播放中心，操控音乐 App+ 歌词副屏管理
- **Android Remote（手机端）：** 点歌遥控器 + 滚动歌词副屏显示

---

## 📱 快速开始

### **第 1 步：启动 Host**

在电脑上运行（已编译版本）：

```bash
# 方式 1: 双击 exe
.\publish\win-x64\SongLink.Host.exe

# 方式 2: 命令行带参数
.\publish\win-x64\SongLink.Host.exe --adapter=localsource
```

**你会看到：**
```
[host] 监听 ws://0.0.0.0:51999/ PIN=123456
适配器=localsource caps=ReadMeta,Transport,Pick,Search
```

记下显示的 **PIN 码**！

---

### **第 2 步：连接手机 App**

#### **方案 A: Python Simulator（调试用）**

```powershell
python outputs/songlink_remote_sim.py ws://localhost:51999 123456
```

#### **方案 B: Android App（正式版）**

1. 下载 APK（待发布）
2. 打开 App → 输入 Host IP（如 `ws://192.168.1.100:51999`）
3. 输入 PIN 码
4. 点击"连接"

**成功后会显示：**
- ✅ "鉴权成功！Token: a1b2c3d4..."
- ✅ "收到 Host Hello: DESKTOP-XXX"

---

### **第 3 步：测试功能**

在手机上尝试以下操作：

1. **查看当前播放状态**  
   - 如果本地库有歌曲 → 应显示 "晴天 - 周杰伦 pos=0.0/269s status=stopped"

2. **搜索歌曲**  
   - 输入"晴天" → 应返回匹配结果列表

3. **点歌**  
   - 点击搜索结果 → PC 应开始播放

4. **控制播放**  
   - 点击"暂停"→"下一首"→"恢复播放"

---

## 🔍 功能详解

### **1. 点歌模式（Pick Page）**

#### **支持的音源类型：**

| 类型 | 描述 | 是否需配置 |
|------|------|-----------|
| **QQMusic** | QQ 音乐（UIA+OCR 兜底） | ✅ descriptor 校准 |
| **NeteaseMusic** | 网易云音乐 | ✅ descriptor 校准 |
| **KugouMusic** | 酷狗音乐（已实测可用） | ✅ 无需额外配置 |
| **LocalSource** | 本地音源文件夹 | ❌ 即开即用 |

#### **操作流程：**
```
手机输入关键词 → Host 返回搜索结果 → 点击选择 → 
Host 调用对应 Adapter.Pick(title, artist) → 
PC 音乐 App 开始播放
```

---

### **2. 队列管理（Queue Management）**

#### **支持的操作：**

- **添加：** `queue.add` → 加入新曲目到队尾
- **置顶：** `queue.top {index}` → 将指定曲目移至队首
- **删除：** `queue.remove {index}` → 移除指定位置
- **清空：** `queue.clear` → 清空所有排队曲目

#### **队列状态更新：**
每次操作后，手机端会收到 `EventFrame{name: queue-updated}` 自动刷新列表

---

### **3. 歌词副屏（Lyrics Page）**

#### **功能特性：**

- ✅ **自动同步**：基于 SMTC position 驱动时间轴
- ✅ **LRC 加载**：优先本地文件，其次联网拉取（LRCLib）
- ✅ **偏移校准**：支持 ±500ms 手动微调
- ✅ **滚动显示**：选中歌词行高亮动画

#### **校准方法：**

1. 播放一首歌曲
2. 如果歌词延迟 → 拖动 Slider 增加 offset（如 300ms）
3. Host 立即重发带新 offsetMs 的 Lyric 帧
4. 手机歌词即时对齐！

---

### **4. 音源模式切换（Source Mode）**

#### **PC_LOCAL（默认）**
- PC 上的音乐 App 或本地音源出声
- 歌词封面从 PC 推送给手机

#### **PHONE_STREAM（手机音频串流）**
- 手机音频经网络串流进 PC
- PC 再用自身蓝牙/音箱输出
- 元数据由手机端上报

#### **切换方法：**
```javascript
await remote.cmd("setSourceMode", {"mode": "PHONE_STREAM"})
// 或
await remote.cmd("setSourceMode", {"mode": "PC_LOCAL"})
```

---

## 🎵 本地音源使用（LocalSourceAdapter）

### **准备阶段**

1. 创建歌曲文件夹：`E:\workspace\点歌台面板\library\`
2. 放入 MP3/WAV/FLAC 格式的音乐文件
3. 命名规范：`歌手 - 歌名.mp3`（如"周杰伦 - 晴天.mp3"）

### **导入歌曲**

#### **方法 A: 拖拽到 library 目录**
直接复制文件进入 `library/` 子目录

#### **方法 B: Host API 调用**
```json
POST /import?title=晴天&artist=周杰伦
Body: <mp3_bytes>
```

#### **方法 C: 批量扫描**
```csharp
var scanner = new LocalFolderScanner();
await scanner.ScanFolderAsync("D:/我的音乐");
```

### **验证效果**

在手机端：
1. 切换到"本地音源"模式
2. 搜索任意歌名 → 应能看到你的收藏曲库
3. 点击播放 → PC 端应立即出声！

---

## 📶 网络与连接

### **局域网发现（mDNS）**

```bash
# 查看可用主机
ping songlink-host.local
```

### **蓝牙配对（RFCOMM）**

如果 Wi-Fi 不可用，可以：

1. PC 开启蓝牙广播：`--adapter=bluetooth`
2. 手机端搜索并配对（输入 PIN）
3. 建立 RFCOMM 连接传输指令
4. **限制：** 仅支持文本数据（无歌词/封面），适合紧急情况下备用

---

## ⚙️ 常见问题解决

### **Q1: 搜索结果为空？**

**原因：** 未找到匹配的第三方 App 或本地音源库为空

**解决：**
- 检查 QQ 音乐/网易云是否正在运行
- 确认 descriptors/*.json 中 OCR 坐标已校准
- 或先导入一些本地 MP3 文件

---

### **Q2: 点歌失败？**

**原因：** UIA 读不到控件 → OCR 兜底失败

**排查步骤：**
1. QQ 音乐窗口是否最大化（确保 OCR 能扫到全部区域）
2. 检查控制台日志是否有 OCR 识别结果
3. 尝试调整 descriptors 中的 resultRegion 大小

---

### **Q3: 歌词不对齐？**

**原因：** SMTC position 有延迟误差

**解决方法：**
1. 手机端长按歌词行 → 弹出校准 Slider
2. 拖动到准确位置
3. Host 立即下发新 lyric 帧生效！

---

### **Q4: 手机连不上 Host？**

**可能原因：**
- Port 51999 被防火墙拦截
- Host 只绑定 localhost（非 0.0.0.0）
- 手机与 PC 不在同一网段

**解决：**
```powershell
# 检查端口占用
netstat -ano | findstr :51999

# 放行防火墙规则
netsh firewall add portopening TCP 51999 "SongLink" ENABLE
```

---

## 🔒 安全建议

1. **设置固定 PIN**（不要随机生成）：
   ```bash
   dotnet run --project src/SongLink.Host.Core --pin="123456"
   ```

2. **启用帧签名模式**（防止同网伪造指令）：
   ```json
   { "RequireFrameSig": true }
   ```

3. **仅允许局域网访问**（非公网暴露）

---

## 💡 高级技巧

### **技巧 1: 双人点歌接力**

两台手机同时连接 Host，轮流点歌，队列自动 FIFO 处理！

### **技巧 2: 歌词主题自定义**

修改 lyrics/lrc 样式文件，或上传自定义字体到手机端渲染层

### **技巧 3: 批量导入专辑**

```bash
cd library
for %f in (album/*.mp3) do @copy "%f" ..
```

---

## 📈 性能调优建议

1. **StateGate 节流频率**：降低 CPU 占用（默认 1Hz 已足够）
2. **ReplayBuffer 容量**：根据内存大小调整（默认 128 条够用）
3. **OCR 兜底开关**：如果纯 UIA 可用，可禁用以减少负载

---

## 🎉 彩蛋功能

- **音量调节：** 通过 WMPLayer.controls.volume 动态调音
- **封面缓存：** 首次点击会自动保存 albumArt 到 art/目录
- **断线重连：** 即使网络波动也能秒级恢复，seq 补发机制保障状态一致性

---

**祝您享受完美的点歌体验！** 🎤🎵

如有任何问题，请查看 docs/api-reference.md 获取完整协议规范。
