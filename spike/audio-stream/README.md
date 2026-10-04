# M0 Spike ② · 手机音频串流到电脑（含蓝牙）输出

对应架构文档 §3.7 / §14、《协议契约.md》§4（音频分片头）。验证链路：

```
手机任意 App 播放 → AudioPlaybackCapture 抓音 → MediaCodec(OPUS) → UDP(帧头+opus) → PC audio-sink 解码 → WASAPI 默认输出(可配蓝牙音箱)
```

## 目录

- `pc/` — **PC 接收端**（.NET 8，纯托管，本机可编译）
  - `audio-sink.csproj`：依赖 `Concentus 2.2.2`（解 Opus）+ `NAudio 2.2.1`（WASAPI 播放）
  - `AudioFrame.cs`：契约 §4 帧头解析（大端 `ver|flags|seq4|pts8|len2|payload`，头 16 字节）
  - `Program.cs`：UDP 收包 → Concentus 解码 → NAudio 播放；含 `selftest` 闭环自检
- `android/` — **手机采集端**（Kotlin 源码脚手架，需 Android 10+ 真机/模拟器编译）
  - `AudioCaptureService.kt`：`MediaProjection` 授权 → `AudioPlaybackCaptureConfiguration` 抓其他 App 音频 → `MediaCodec` OPUS 编码 → 按 §4 打包 → `DatagramSocket` 发往 PC

## 已在本机验证（PC 侧）

```bash
cd pc
dotnet build            # ✅ 0 error 0 warning（Concentus+NAudio 纯托管，无 WinRT）
dotnet run -- selftest  # ✅ 编码→打包→解析→解码闭环：样本数>0 且 RMS>0 → PASS
```

`selftest` 不依赖声卡/网络，证明**帧头契约与 Opus 编解码通路正确**；`dotnet run -- 51000` 才是真监听并出声（需默认播放设备，把它设为蓝牙音箱即实现“经电脑蓝牙输出”）。

## 端到端联调（需安卓设备）

1. PC：`cd pc && dotnet run -- 51000`（记下本机局域网 IP；把默认输出设为蓝牙音箱）。
2. 手机：在 SongLink Remote 里启动 `AudioCaptureService`，传入 `MediaProjection` 授权结果 + PC 的 `host:port`。
3. 手机随便放首歌 → 声音应从 PC 的蓝牙音箱出来（有 ~200–400ms 延迟，属“播放”非“监听”，见 R11）。
4. 抓不到（DRM/受保护内容）→ 切“路径B”：用 Remote 内置播放器放，链路最稳（见 R10）。

## 帧格式（务必与《协议契约.md》§4 一致）

```
偏移  长度  字段         说明
0     1     ver          =1
1     1     flags        bit0 首片, bit1 末片
2     4     seq          大端，丢帧检测
6     8     pts          大端，帧呈现时间戳（样本号）
14    2     payloadLen   大端，opus 字节数
16    N     opus payload 一帧 20ms Opus
```

## 注意 / 待办

- `MediaFormat.KEY_AAC_PROFILE` 那行是占位，真机接 Opus 编码器参数（复杂度/包络）按 `MediaCodecInfo.CodecCapabilities` 调整。
- 蓝牙只出现在 PC→音箱这段；手机→PC 一律走局域网 UDP（Windows 不能当蓝牙音频接收端）。
- 抖动缓冲/丢包 PLC/码率自适应是 M2.5 的活，本 spike 只做最小通路验证。
