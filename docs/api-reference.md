# SongLink API Reference v1.0

## 📋 协议概览

**位置：** `src/SongLink.Protocol/Frames.cs`  
**格式：** JSON + WebSocket（文本帧）  
**版本：** v1（唯一正式版本）

---

## 🔌 连接建立

### **1. 握手流程**

```
Client → Host: { "type": "hello", "v": 1, ... }
Host   → Client: { "type": "authResult", "ok": true, "token": "...", "hostKey": "..." }
Host   → Client: { "type": "hello", "role": "host", "device": "DESKTOP-XXX" }
```

### **2. Hello 帧结构**

```json
{
  "v": 1,
  "type": "hello",
  "id": "h-1728690000000",
  "role": "remote",                    // 必选："remote" | "host"
  "device": "Pixel-7-Pro",             // 可选：设备标识
  "os": "Android",                     // 可选：操作系统
  "caps": ["cmd", "state", "lyric", "art"],  // 能力列表
  "pin": "123456",                     // 首次配对：PIN 码
  "token": "a1b2c3d4...",             // 重连：Token
  "lastSeq": 28                        // 断线补发：上次已接收的 seq
}
```

### **3. AuthResult 帧结构**

```json
{
  "v": 1,
  "type": "authResult",
  "id": "h-1728690000000",              // 对应 Hello.id
  "ref": "",
  "ok": true,                           // 鉴权结果
  "token": "a1b2c3d4e5f6...",          // 长期访问令牌
  "hostKey": "fedcba9876543210",       // 帧签名密钥（可选）
  "code": "",                           // 失败原因（如 "AUTH_FAILED"）
  "hostInfo": {                         // 主机信息
    "device": "DESKTOP-XXX",
    "os": "Windows",
    "version": "1.0.0"
  }
}
```

---

## 🎵 核心帧类型

### **State 帧（当前播放状态）**

```json
{
  "v": 1,
  "type": "state",
  "seq": 28,                            // 全局序列号（单调递增）
  "ts": 1728690000.123,                 // 服务器时间戳
  "now": {
    "title": "晴天",
    "artist": "周杰伦",
    "album": "十一月的萧邦",            // 可选
    "pos": 15.5,                        // 当前位置（秒）
    "dur": 269.0,                       // 总时长（秒）
    "status": "playing",                // playing | paused | stopped
    "artRef": "img:sunny-day-0",        // 封面引用（可选）
    "sourceMode": "PC_LOCAL"            // PC_LOCAL | PHONE_STREAM
  },
  "sig": ""                             // HMAC 签名（可选）
}
```

### **Lyric 帧（歌词数据）**

```json
{
  "v": 1,
  "type": "lyric",
  "trackId": "lrc:晴天 - 周杰伦",
  "offsetMs": 0,                        // 偏移量（毫秒），支持校准
  "lines": [
    {"t": 0.0, "x": "编制"},           // t: 时间（秒）, x: 歌词文本
    {"t": 2.5, "x": "作曲：周杰伦"},
    {"t": 4.8, "x": "作词：方文山"}
  ]
}
```

### **Cmd 帧（客户端命令）**

#### **基础控制**
```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000001",
  "action": "play",                     // play | pause | stop | next | prev | toggle
  "args": {}
}
```

#### **点歌**
```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000002",
  "action": "pick",
  "args": {
    "title": "晴天",
    "artist": "周杰伦"
  }
}
```

#### **搜索**
```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000003",
  "action": "search",
  "args": {
    "kw": "晴天"
  }
}
```

#### **队列管理**
```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000004",
  "action": "queue.add",
  "args": {
    "title": "Yellow",
    "artist": "Coldplay"
  }
}
```

```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000005",
  "action": "queue.top",
  "args": {
    "index": 2
  }
}
```

```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000006",
  "action": "queue.clear"
}
```

#### **源模式切换**
```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000007",
  "action": "setSourceMode",
  "args": {
    "mode": "PHONE_STREAM"              // PC_LOCAL | PHONE_STREAM
  }
}
```

#### **歌词校准**
```json
{
  "v": 1,
  "type": "cmd",
  "id": "c-1728690000008",
  "action": "calibrate",
  "args": {
    "lyricOffsetMs": 300,               // 延迟（毫秒）
    "title": "晴天",
    "artist": "周杰伦"
  }
}
```

### **Ack 帧（命令响应）**

```json
{
  "v": 1,
  "type": "ack",
  "id": "c-1728690000001",              // 对应 Cmd.id
  "ref": "",
  "ok": true                            // 执行结果
}
```

### **ErrorFrame 帧（错误通知）**

```json
{
  "v": 1,
  "type": "error",
  "id": "c-1728690000001",              // 对应 Cmd.id
  "ref": "",
  "code": "APP_NOT_FOUND",              // AUTH_FAILED | BAD_PROTO | PICK_UNSUPPORTED 等
  "hint": "请确认目标音乐 App 已安装并运行",  // 友好提示
  "msg": ""
}
```

### **SearchResults 帧（搜索结果）**

```json
{
  "v": 1,
  "type": "searchResults",
  "id": "c-1728690000003",
  "query": "晴天",
  "results": [
    {
      "title": "晴天",
      "artist": "周杰伦",
      "album": "十一月的萧邦",
      "dur": 269.0
    }
  ],
  "total": 1,
  "source": "qqmusic|netease|localsource"  // 来源标记
}
```

### **EventFrame 帧（事件广播）**

```json
{
  "v": 1,
  "type": "event",
  "name": "queue-updated",              // queue-updated | source-mode-changed | adapter-caps-changed
  "mode": "PHONE_STREAM",               // 模式切换时携带
  "queue": [                            // 队列更新时携带
    { "title": "Yellow", "artist": "Coldplay", "by": "remote" },
    { "title": "稻香", "artist": "周杰伦", "by": "local" }
  ]
}
```

### **Art 帧（封面引用）**

```json
{
  "v": 1,
  "type": "art",
  "ref": "img:sunny-day-0",
  "mime": "image/png",                  // image/png | image/jpeg | image/webp
  "size": 1579                          // 文件大小（字节）
}
```

**注意：** 封面文件通过 HTTP GET 拉取，不走 WebSocket 帧传输：
```http
GET /art/img:sunny-day-0 HTTP/1.1
Host: localhost:51999
```

### **封面上传（HTTP，非 WebSocket 帧）**

手机正在播放的封面要显示到电脑面板上时，手机把图片直接 POST 给主机存进 art 目录，
面板再用上面的 `GET /art/{ref}` 拉下来。

```http
POST /art?name={ref} HTTP/1.1
Host: localhost:51999
Content-Type: image/png        # image/png | image/jpeg | image/webp

<原始图片字节>                  # 不是 multipart，也不是 base64
```

| 参数 / 情况 | 结果 | 说明 |
|------|--------|------|
| 成功 | `200` | 响应体纯文本 `ok` |
| `name` 缺失 / 为空 | `400` | ref 由客户端生成，服务端只校验 |
| `name` 含 `..` 或非法字符 | `400` | `ArtStore.IsSafeRef`：只允许 `[A-Za-z0-9._-]` |
| `name` 扩展名不在白名单 | `400` | 仅 `.png` / `.jpg` / `.jpeg` / `.webp`（`ArtStore.Exts`） |
| `Content-Type` 不支持 | `400` | 不在上述三种之内就不读 body |
| 请求体 > 2 MB | `413` | 封面用不着更大 |
| 写盘失败 | `500` | 不返回异常细节 |

```bash
curl -X POST "http://localhost:51999/art?name=sunny-day-0.png" \
     -H "Content-Type: image/png" \
     --data-binary @cover.png
# → ok
```

⚠ **该端点不鉴权**（与既有 `GET /art/{ref}` 保持一致）：同网段任意设备都能往 art 目录写图。
当前风险可控是因为 ref 字符集受限、写不进可执行文件、且有 2 MB 上限；
一旦 Host 暴露到公网，必须补上 token 鉴权与配额/清理策略。

---

## 🔐 安全机制

### **1. PIN 码配对**

```json
// Client → Host（首次连接）
{"v": 1, "type": "hello", "id": "h1", "pin": "123456", ...}

// Host → Client
{"v": 1, "type": "authResult", "id": "h1", "ok": true, "token": "..."}
```

### **2. Token 重连**

```json
// Client → Host（重连）
{"v": 1, "type": "hello", "id": "h2", "token": "a1b2c3...", ...}
```

### **3. HMAC-SHA256 帧签名**（契约 §6）

#### **入站验证**
Host 收到 `cmd` 帧时：
```javascript
expectedSig = HMACSHA256(token, canonicalize(cmd))
if (frame.sig !== expectedSig) return Error("AUTH_FAILED")
```

#### **出站签名**
Host 发送广播帧时：
```csharp
frame.Sig = FrameSigner.Compute(hostKey, frame)
```

---

## 🔄 断线补发机制（契约 §7）

### **原理**
- Host 维护最近 128 条可重放广播帧（ReplayBuffer）
- 客户端重连时携带 `lastSeq` 参数
- Host 返回所有 `seq > lastSeq` 的帧

### **示例**
```javascript
// 重连请求
{"v": 1, "type": "hello", "id": "h3", "lastSeq": 28}

// Host 补发 seq=29,30,31... 的 State 帧
```

---

## 📊 性能指标

| 指标 | 预期值 | 备注 |
|------|--------|------|
| State 广播频率 | ≤ 1Hz | StateGate 节流 |
| 单帧大小 | < 8KB | 蓝牙友好 |
| 平均延迟 | < 50ms | Wi-Fi 环境 |
| 断线重连补发 | ≤ 2s | 最近 128 条内 |

---

## ⚠️ 限制与约束

1. **Protocol Version:** 仅支持 v1，不支持向后兼容降级
2. **UTF-8 编码:** 所有帧必须是 UTF-8 编码
3. **JSON 属性顺序:** System.Text.Json要求 `"type"` 为首属性（第三方客户端需手动调整顺序）
4. **WebSocket SubProtocol:** 不支持扩展协议（仅文本帧）
5. **最大连接数:** 建议 ≤ 5 个同时连接
6. **音频流：** 不在 WebSocket 通道内，单独 UDP/WebSocket 处理

---

## 🧪 测试工具

```python
# Python Remote Simulator
python outputs/songlink_remote_sim.py ws://localhost:51999 123456

# 简易测试
python outputs/songlink_simple_test.py
```

```powershell
# 自测试
dotnet run --project src/SongLink.Host.Core -- selftest
```

---

**此文档持续更新，以代码实现为准。** 🎵
