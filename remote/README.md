# SongLink Remote（安卓遥控端 · M1 脚手架）

架构见 `../docs/技术方案与架构设计.md`；协议见 `../docs/协议契约.md`。
本目录是安卓遥控端（点歌 + 副屏显示歌词/封面）的**源码脚手架**。

> ⚠ 本仓库开发环境无 Android SDK，此模块为源码交付；用 Android Studio（含 SDK 34）打开 `remote/` 即可编译运行。协议层与已在本机跑通的 `../src/SongLink.Protocol` 严格对应。

## 已包含

- `data/Protocol.kt` — 协议帧的 Kotlin 镜像（kotlinx.serialization 多态，`type` 判别符），与 `docs/schema/songlink-frame.schema.json` 一一对应。
- `data/HostClient.kt` — OkHttp WebSocket 客户端：`connect(url)`、`frames` 收帧流、`sendHello/sendCmd/sendSrcMeta`。
- `ui/NowPlayingScreen.kt` — 副屏 Compose：当前曲 + 按 `pos` 高亮滚动歌词 + 上一首/播放暂停/下一首。
- `app/build.gradle.kts` / `AndroidManifest.xml` — 依赖与权限（局域网 + 蓝牙 + 音频串流所需）。

## 与 PC 端联调（本机已验证协议）

PC 端起 Host（见 `../src/README.md`）：
```bash
cd ../src/SongLink.Host.Core && dotnet run -- run 51999
```
安卓端（拿到 PC 局域网 IP 后）：
```kotlin
val client = HostClient(scope)
client.connect("ws://192.168.x.x:51999/")
client.sendHello(device = Build.MODEL, caps = listOf("cmd","state","lyric","art"))
// 订阅 client.frames 渲染副屏；按钮回调 client.sendCmd("next") 等
```
`../src/SongLink.RemoteSim` 是本协议在桌面端的等价实现，已实测与 Host 端到端跑通（hello/cmd/ack/state/lyric/srcMeta 全通），可作为安卓实现的参照与回归对照。

## 待补（M1-β / M2）

- `MainActivity.kt` + 导航：配对页（输入/发现 PC）→ 点歌页 → 副屏页。
- 封面加载：`art` 帧的 `ref` → Coil 拉 `http://<pc>/art/<ref>`。
- 点歌搜索 UI：`cmd:search` → 结果列表 → `cmd:pick`。
- 蓝牙配对引导与降级（`caps` 协商，蓝牙模式关封面）。
- M2.5：接 `../spike/audio-stream/android/AudioCaptureService.kt`，`PHONE_STREAM` 下 `sendSrcMeta` 上报手机元数据。
