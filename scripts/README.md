# Scripts

手动 / 半自动验证脚本(不进 CI — 需要本机启 Host CLI 或外部设备)。

## audio-e2e.py — Python 模拟 Android 推流(快速)

WS 连 `ws://127.0.0.1:51999/` + 鉴权 + 发 `audio:start` + 推 100 帧 §4 PCM 直通帧
(`FLAG_PCM_RAW=1`)到指定端口。验证 Host CLI 端 UDP 监听器 + 帧计数是否真起来。

**用法:**

```bash
# 1) 另开一个终端启 Host
dotnet run --project src/SongLink.Host.Core -- run --port 51999 --adapter fake --pin 246810

# 2) 跑这个脚本(默认推 100 帧到 51000)
python scripts/audio-e2e.py
```

成功时 Host CLI 应输出:

```
[audio] AudioStreamSink 启动,UDP listening on 0.0.0.0:51000/ (audio:start.port=51000)
[audio] FramesReceived=50, 最近payloadLen=1920
[audio] FramesReceived=100, 最近payloadLen=1920
```

## audio-e2e-slow.py — 同上但加 10ms/10 帧节流

`audio-e2e.py` 突送 100 帧可能丢(Windows 默认 UDP 接收缓冲 8KB,
1 秒 48k mono PCM ≈ 192KB)。`audio-e2e-slow.py` 加节流验证完整接收。

**用法:**

```bash
# 推 200 帧到 51000 (argv 顺序: port dummy n_frames)
python scripts/audio-e2e-slow.py 51000 dummy 200
```

期望 Host 输出 `FramesReceived=50/100/150/200`(无丢失)。

## audio-1frame.py — 推 1 帧调试

发送 1 帧到 Host,用于验证最简 wire format 是否被解析。

## test-udp.py / test-udp2.py — 裸 UDP 探测

`test-udp.py` 推 1 帧 1936B;`test-udp2.py` 推 N 帧带节流。不走 WS,
只验证 UDP socket 通路是否通,用于排查 Python → .NET UDP 是否被防火墙阻挡。

## udp-listener.ps1 — PowerShell UDP 监听

`C:\Users\Admin\udp-listener.log` 写每次收到的包头(前 16 字节 hex)。用作对照接收端,
确认 `test-udp*.py` 推的字节确实是预期的 §4 帧。

**用法:**

```powershell
# 后台跑
powershell -ExecutionPolicy Bypass -File scripts/udp-listener.ps1
# 收包看 log
Get-Content C:\Users\Admin\udp-listener.log -Wait
```

## test-parse.csx — dotnet-script 单元解析

`dotnet script scripts/test-parse.csx` 直接调 `AudioStreamWire.Parse` 验证一个
已知字节流解析结果(需要安装 `dotnet-script` 全局工具,默认未装)。

---

**为什么不在 CI?** 这些脚本要么需要外部进程(Host CLI 在跑),要么需要外部设备
(Android 真机 + 蓝牙音箱 + 第二显示)。CI 跑不动,留给手动验证用。
单元 / 集成覆盖在 `src/SongLink.Host.Tests/` 和 `src/SongLink.Protocol.Tests/`。
