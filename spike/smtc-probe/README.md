# M0 Spike · SMTC 探针 (smtc-probe)

点歌台(SongLink)立项的第一个去风险实验。验证整套系统骨架里**最核心、且跨 App 稳定**的那一段:能否用 Windows 系统级 **SMTC** 读到"正在播放"的曲名/艺人/专辑/封面/进度,并下发播放/暂停/上下曲——**不依赖任何音乐 App 的私有 API**。

对应架构文档:`docs/技术方案与架构设计.md` §3.1 / §3.2 / §6.1 / §13(S3·S12·S1 读侧) / §10 M0。

## 它能做什么

```
dotnet run -- status    # 读当前播放:来源 App / 曲名 / 艺人 / 专辑 / 播放状态 / 进度,并把封面存到 bin/.../art/
dotnet run -- watch     # ~1.4Hz 采样 PlaybackPosition,验证进度平滑到能驱动歌词滚动
dotnet run -- list      # 列出所有活跃 SMTC 会话
dotnet run -- next      # 下一首   dotnet run -- prev   上一首
dotnet run -- play / pause / toggle / stop
dotnet run -- dump      # 把所有会话的能力字段序列化为 JSON（供能力矩阵用）
```

跑之前先在你的音乐 App(QQ音乐/网易云/酷狗/Spotify/Apple Music…)里随便放一首歌。

## 判读标准

- `status` 能打出曲名/艺人/专辑/进度 → **§3.1 只读真值链路成立**,副屏体验底座可复用(S3/S12/S1)。
- `watch` 里进度每秒稳定递增、Δ≈采样间隔 → 歌词时间轴同步可行(R7 有解)。
- `next/prev/pause` 返回 `True` 且被控 App 真的响应 → **§3.2 传输控制成立**。
- 某条控制返回 `False` → 说明该 App 没实现这个 SMTC 按钮;点歌台据此在能力探针(§6.1 `Caps`)里把它标灰,不影响其它功能。
- 注意:本探针**不含**"点任意指定歌"(那是最难的 §3.3,需要每 App 适配器 UIA/OCR,另做 spike)。

## 关键实现点(踩过的坑)

- SMTC 类型在 **`Windows.Media.Control`** 命名空间(`GlobalSystemMediaTransportControlsSessionManager` 等),**不是** `Windows.Media.Playback`。
- 取封面:`session.ThumbnailProperties.OpenReadAsync()` → `RandomAccessStream.CopyAsync` 落盘。
- 传输控制用 session 的 `TryPlayAsync()/TryPauseAsync()/TrySkipNextAheadAsync()/TrySkipPreviousAsync()/TryStopAsync()`,返回 `IAsyncOperation<bool>`。

## 环境准备(重要)

本项目用标准 `net8.0-windows10.0.19041.0`,依赖 **.NET 的 Windows SDK 投影 pack**(`Microsoft.Windows.SDK.NET.Ref`)来暴露 `Windows.*` WinRT 类型。

- **在装了 Visual Studio「Windows 应用程序开发」工作负载 / Windows App SDK 的机器上**:直接 `dotnet build && dotnet run -- status` 即可,SDK 会自动接好投影。
- **在精简版 .NET SDK 机器上**(本机 `C:\Program Files\dotnet\packs` 下没有 `Microsoft.Windows.SDK.NET.Ref`):编译会报"找不到 Windows.Media.* 类型"。三选一解决:
  1. 用 Visual Studio 的 .NET 桌面安装(带 Windows 投影组件);
  2. 安装 `Microsoft.WindowsAppSDK` 对应运行环境;
  3. 换一台标准开发机跑本 spike。
  > 备注:曾尝试"手动 `Reference` 投影 DLL"绕过,能让类型/命名空间解析,但 WinRT 运行时类的属性成员需要 pack 与 winmd 元数据协作才能出现,手引 DLL 补不上成员(故最终仍用标准 TFM)。`refs/` 目录是那次尝试的残留,可删。

## 能力矩阵管线（dump → matrix）

在开发机上一次性采集所有正在播放的 App 能力，再离线生成矩阵：

```bash
# 1) 开发机（需 Windows 投影 pack）：在各 App 里都放着歌，导出会话快照
cd spike/smtc-probe && dotnet run -- dump > smtc-dump.json

# 2) 任意机器（纯托管，本机即可跑）：把 JSON 变成能力矩阵表 + CSV
cd spike/smtc-matrix && dotnet run -- smtc-dump.json
#   → 打印 App×{Title/Artist/Album/封面/进度/状态} 矩阵，并写 smtc-capability-matrix.csv
```

`smtc-matrix` 不依赖 WinRT，已在本仓库样例 `sample-dump.json` 上跑通；`smtc-probe dump` 负责真机采集。两者拼成 §6.1 能力探针 / §10 M0 的“逐个 App 支持度”数据。

## 一句话

这个 spike 只回答一个问题:**"SMTC 能不能当点歌台的跨 App 播放真值源?"** 在正常开发机上跑 `status` 三秒见分晓。跑通了,架构文档里 S1/S3/S12 这条主干就从"推断"升级为"实测"。
