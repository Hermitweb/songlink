# 手机扫码配对 — 完整流程

## 端到端流程

```
┌──────────────────────┐                ┌──────────────────────┐
│  PC 桌面 app          │                │  Android 手机          │
│  (SongLink.exe)      │                │  (SongLink Remote)    │
│                      │                │                      │
│  1. 启动             │                │  1. 装 APK            │
│  2. 显示 PIN         │                │  2. 打开 App          │
│  3. Web 面板"手机连" │   QR 扫描      │  3. 点"扫码连接"     │
│  4. 生成 QR 图       │  ───────────→  │  4. 扫到 wsUrl+pin    │
│  5. (你扫)           │                │  5. 自动连到 PC       │
│  6. (手机)           │                │  6. 控制 / 同步完成   │
└──────────────────────┘                └──────────────────────┘
```

## 步骤详解

### 1. PC 端准备

- 启动 `dist\SongLink-win-x64\SongLink.exe`
- 状态栏会显示 `PIN: 6位数字  端口: 51999`
- 默认绑定 localhost,手机需要 PC 和手机在同一 WiFi

#### 局域网绑定(手机能扫的前提)

如果手机不在同一台机器,需要:
- 用管理员启动 SongLink.exe,或
- 以管理员执行 `netsh http add urlacl url=http://+:51999/ user=%USERNAME%` 后启动

### 2. Web 面板"手机连"按钮

- 浏览器打开 `http://localhost:51999/?pin=<6位PIN>`
- 顶部右上角 📱 按钮 → 弹出 QR 模态
- **真 QR 图**(服务端 `QRCoder` 生成)
- 内容:JSON `{"wsUrl":"ws://192.168.x.x:51999/","pin":"XXXXXX"}`

### 3. Android 端扫码

- 安装 `mobile\android-remote\app\build\outputs\apk\debug\app-debug.apk`
- 打开 → 主页 → 点"扫码连接"
- 申请相机权限(Android 6+ 需要)
- 扫描 PC 端 QR → 解析 JSON → 拿到 wsUrl + pin
- 自动跳转到配对页面(已经填好 URL + PIN,直接点连接)

### 4. 验证连接

- Android 端显示"已连接"
- PC 端控制台日志:`[host] 客户端接入 #xxx`
- Android 端"音频串流"页可推 PCM 到 PC
- Web 面板右上角"连接"状态由红变绿

## 当前实现状态

| 模块 | 状态 |
|---|---|
| PC 端 QR 生成 | ✅ QRCoder 1.6.0 集成,`GET /qr?text=...&size=N` 端点工作 |
| Web 面板 QR modal | ✅ 显示真 QR 图(测试 png 输出 542B,有效) |
| Android QR 解析 | ✅ `parseQrData` 支持 `wsUrl+pin` 新格式 + `host+pin` 旧格式 |
| Android 相机扫描 | ⚠️ 占位实现(UI 完整,ZXing 解码逻辑在,但 CameraX 预览未接入) |
| Android 自动连 | ⚠️ onScanSuccess 回调存在,但需要 Pair 页面完整流程串通 |

## 已知未完事项

1. **Android 相机预览未接入** — `QrCodeScanPage` 拿到的是 `capturedUri`(从相册/Intent),真机扫码需 CameraX 实时预览 + ZXing 持续解码
2. **Android Pair 页面解析 QR 后自动连接** — 现在 `onScanSuccess(host, pin)` 只是 `popBackStack`,需要把 host/pin 写入 Pair 页面输入框
3. **PC 端确认 Android 设备已配对** — `Hello` 帧的 `caps: ["mobile"]` 应能区分 Android vs Web 客户端

## 手动配对(扫码不通时的兜底)

如果扫码不行,Android 端"扫码连接"页可手动输入:
- 主机地址: `ws://192.168.x.x:51999/`
- PIN 码: PC 桌面 app 状态栏里的 6 位数字

## 验收测试(下次有真机时)

- [ ] Android 手机(API 30+)通过 WiFi 扫到 PC QR
- [ ] 自动跳到 Pair 页面,输入框已填 wsUrl + pin
- [ ] 点"连接" → 显示"已连接"
- [ ] Android 端"音频串流"页启动 MediaProjection → 推送 PCM 到 PC
- [ ] PC Web 面板可看到音量条/当前播放状态在变化
