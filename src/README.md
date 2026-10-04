# SongLink 点歌台 · 源码（M1 可运行骨架 · 含组合根与音频串流）

架构见 `../docs/技术方案与架构设计.md`。本目录是 M1 第一刀:**把 Host 中枢做成能真跑的纵向切片**,
用纯托管核心证明"局域网端到端协议闭环"成立,再把 WinRT/安卓等不可本机验证的部分以源码交付。

## 工程结构

| 项目 | TFM | 依赖 | 本机可编译/运行 | 作用 |
|---|---|---|---|---|
| `SongLink.Protocol` | net8.0 | 无 | ✅ | 通信帧类型 + JSON 多态编解码 + `AudioFrame`(§4)（对齐 `docs/协议契约.md` / `schema`） |
| `SongLink.Host.Core` | net8.0-windows | Protocol, AudioStream | ✅ | HttpListener WebSocket 服务器 + Media Bus + `IPlayerAdapter` + `FakeAdapter` + 鉴权/歌词/封面/重放/组合根 |
| `SongLink.Host.AudioStream` | net8.0-windows | Protocol, Concentus, NAudio | ✅ | 手机音频串流接收：UDP→Concentus 解 Opus→`IAudioRenderer`(WASAPI/Null) |
| `SongLink.RemoteSim` | net8.0 | Protocol | ✅ | 命令行扮演安卓遥控端（集成测试/手动验证台） |
| `SongLink.Protocol.Tests` | net8.0-windows | Protocol, Host.Core, AudioStream | ✅ | 47 用例：schema 契约 + 组合根 + 鉴权/歌词/封面/重放 + 音频回路 |
| `SongLink.Host.Adapters.Smtc` | net8.0-windows10.0.19041.0 | Protocol, Host.Core | ⚠ 需开发机（Windows 投影 pack） | 真实 SMTC 适配器，实现同一 `IPlayerAdapter` |

## 本机已实测

```bash
# 1) 协议闭环自检（单进程，起 Host+连客户端+断言）
cd SongLink.Host.Core && dotnet run -- selftest
#   ✓ 未鉴权指令→AUTH_FAILED  ✓ 正/误 PIN→authResult  ✓ next/pause→ack  ✓ state=paused
#   ✓ pick→ack  ✓ lyric 帧  ✓ srcMeta→PHONE_STREAM  ✓ 未鉴权不收广播  ✓ GET /art/demo-0→200  ✓ 未知 ref→404

# 2) 双进程端到端联调
cd SongLink.Host.Core && dotnet run -- run 51999            # 终端 A：起 Host（FakeAdapter），控制台打印 PIN
cd SongLink.RemoteSim && dotnet run -- --url ws://localhost:51999/ --pin <上面打印的PIN> --cmds "next,pause,pick:Yellow" --seconds 3
#   → RemoteSim 先 authResult(ok+token)，再收 ack + state（变化即时、稳态 ≤1Hz 心跳），pick 后切歌
#   封面：GET http://localhost:51999/art/demo-0 → image/png
```

## 设计要点（对应架构）

- **核心与出声方解耦**：`Host.Core` 只认 `IPlayerAdapter`；`FakeAdapter`（本机跑）与 `SmtcAdapter`（真机）实现同一接口，Host 代码零改动即可切换。
- **SMTC 只做只读+传输控制**：`SmtcAdapter.Caps = ReadMeta|Transport`，`Pick` 恒返回 false——"点任意歌"留给 QQ/网易云专属适配器（架构 §3.3/§6.1 的高风险区）。
- **SMTC 事件驱动（评审 #10）**：`SmtcAdapter` 仅首次 `RequestAsync` 并订阅 `CurrentChanged`/`MediaPropertiesChanged`/`PlaybackInfoChanged` 缓存元数据，`ReadNowPlaying` 读缓存 + 墙钟外推进度（无每拍 WinRT 调用），使 Media Bus 轮询变廉价。
- **搜索闭环（评审 #5）**：`cmd:search` → 适配器 `Search`（无能力回 `SEARCH_UNAVAILABLE`）→ `searchResults` 帧；`FakeAdapter` 内置曲库可搜。
- **Media Bus = 单一真值仲裁 + 去抖（§6.3/§14.2，评审 #3/#12）**：`SourceMode` 互斥——`PC_LOCAL` 轮询活动适配器、`PHONE_STREAM` 由手机 `srcMeta` 驱动且进入时 `Pause` 冻结 PC 出声（避免抢声）；`StateGate` 判"变化即发/稳态 ≤1Hz" 后广播 `state`。
- **鉴权（评审 #6）**：`AuthManager` PIN 配对 + token 重连；未鉴权连接只允许 `hello`，且不接收任何广播（防泄露）。
- **断线补发（契约 §7）**：`SendTo/Broadcast` 在**全局锁内原子地"赋 seq + 入队 + 入 `ReplayBuffer`"**，既保证每连接 seq 严格单调（强化 #7），又让重连带 `hello.lastSeq` 时补发 `seq>lastSeq` 的广播（state/lyric/event）；点对点帧不补发。
- **封面（评审 #4）**：`ArtStore` 按 ref 取图（含路径穿越防护），`GET /art/{ref}` 下发，`NowPlaying.artRef` 指向它。
- **唇同步 offset（评审 #11）**：`calibrate` 存每曲/全局偏移，`lyric.offsetMs` 随帧下发，calibrate 后立即重发当前歌词；offset 机制本机已测，真正校准靠人耳在真机微调。
- **协议即契约**：帧类型/字段与 `docs/schema/songlink-frame.schema.json` 一一对应，`SongLink.Protocol.Tests` 每次 `dotnet test` 校验真实发出的帧防漂移。
- **音频串流并入 Host（架构 §14）**：`SongLink.Host.AudioStream` 的 `AudioStreamSink`（UDP→Concentus 解 Opus→`IAudioRenderer`）由 HostServer 在 `audio:start/stop` 时启停；`setSourceMode(PC_LOCAL)` 会停 sink 交还输出。渲染器可注入——真机 `WasapiRenderer`（→PC 蓝牙/音箱），CI/无声卡用 `NullRenderer` 跑解码回路（本机 47 项含此）。

## 组合根配置化（评审后续）

`Host.Core` 的 `run` 入口即组合根，由 `HostConfig` + `AdapterFactory` 驱动：

- 配置来源优先级：命令行 `--port/--adapter/--assembly/--type/--pin` &gt; `appsettings.json` &gt; 默认（`adapter=fake`, `port=51999`, `pin=null` 自动生成）。
- `AdapterFactory` 内置 `fake`（本机可跑）；其余（如 `smtc`）通过**反射按配置加载外部程序集**——Host.Core 不硬依赖 WinRT 适配器工程，因此本机可编译/测试（47 项含组合根与音频回路），开发机换真 SMTC 只改配置、不动代码。
- 未知适配器且未给 `adapterAssembly` → 清晰报错退出（不静默回落）。

```bash
# 本机默认（fake）
dotnet run -- run --port 51999
# 开发机切真 SMTC（先构建 Smtc 工程，把 dll 放到 Host 输出目录或给绝对路径）
dotnet run -- run --adapter smtc --assembly SongLink.Host.Adapters.Smtc.dll --type SongLink.Host.Adapters.Smtc.SmtcAdapter
```

## 在开发机上接真机（M1-β 起点）

1. 装带「Windows 应用程序开发」工作负载的 VS / Windows App SDK，使 `SongLink.Host.Adapters.Smtc` 可编译（`dotnet build src/SongLink.Host.Adapters.Smtc`）。
2. 组合根已就位：构建 Smtc 后，用上面的 `--adapter smtc --assembly … --type …`（或写进 `appsettings.json`）即切到真实 SMTC，无需改代码。
3. 安卓 `Remote` 用 `docs/协议契约.md` 的帧模型连 `ws://<PC-IP>:51999/`，输 PIN 配对，即可复用同一 Host。

## 已知边界

- `SmtcAdapter` 在本机因缺 Windows 投影 pack 报 12 个 `CS1061`（成员/事件不可见），**非代码缺陷**，开发机上消失。
- `NowPlaying.Dur` SMTC 不直接提供，暂留 0，由歌词/元数据层补。
- 音频串流已并入 Host（`SongLink.Host.AudioStream`，UDP→Concentus 解码回路本机 47 项测试验证）；但 `WasapiRenderer` 真实出声需声卡、手机侧 `AudioPlaybackCapture` 抓取需 Android 10+ 真机（见 `spike/audio-stream`）。
