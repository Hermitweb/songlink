#!/usr/bin/env python3
"""
手动集成测试:模拟 Android 端推 PCM 音频到 Host。
- WS 连 127.0.0.1:51999,PIN 246810
- 发 hello 鉴权
- 发 audio:start (rate=48000,ch=1,frameMs=20)
- 等 Host ack
- 构造 100 帧 §4 格式 UDP 包 (FLAG_PCM_RAW=1) 推往 Host 公布端口
- Host stdout 应每 50 帧打印 [audio] FramesReceived=N
"""

import asyncio, json, socket, struct, sys, time

WS_URL = "ws://127.0.0.1:51999/"
PIN = "246810"
N_FRAMES = 100
RATE = 48000
CH = 1
FRAME_MS = 20
SAMPLES_PER_FRAME = RATE * FRAME_MS // 1000   # 960 samples
PAYLOAD_BYTES = SAMPLES_PER_FRAME * 2 * CH    # 1920 B (16-bit LE)

# §4 header: ver(1)|flags(1)|seq(4)|pts(8)|payloadLen(2) = 16 B
WIRE_VERSION = 1
FLAG_FIRST = 0x01
FLAG_LAST  = 0x02
FLAG_RAW_PCM = 0x04

def make_frame(seq: int, pts: int, flags: int, payload: bytes) -> bytes:
    return struct.pack(">BBHIQ H",
                       WIRE_VERSION,
                       flags,
                       seq,
                       pts,
                       payload.__len__()) + payload
# 实际格式:ver(1) flags(1) seq(4) pts(8) payloadLen(2)
# 让我们用更直白的方式拼
def make_frame_v2(seq: int, pts: int, flags: int, payload: bytes) -> bytes:
    h = bytes([WIRE_VERSION, flags]) \
        + struct.pack(">I", seq) \
        + struct.pack(">Q", pts) \
        + struct.pack(">H", len(payload))
    return h + payload

async def main(host_port: int):
    import websockets
    async with websockets.connect(WS_URL) as ws:
        # 1) hello
        hello = {"type":"hello","role":"remote","device":"audio-e2e-py","caps":["cmd"],"pin":PIN,"id":"h-audioe2e","seq":1}
        await ws.send(json.dumps(hello))
        auth = json.loads(await ws.recv())
        print(f"[1] auth: ok={auth.get('ok')} ref={auth.get('ref')} token={'YES' if auth.get('token') else 'NO'}")
        assert auth.get("ok") is True, "auth failed"

        # 2) audio:start
        astart = {"type":"audio:start","id":"a-audioe2e","rate":RATE,"ch":CH,"frameMs":FRAME_MS,"port":host_port,"seq":2}
        await ws.send(json.dumps(astart))
        # 等 ack 或 event
        ack = None
        for _ in range(10):
            raw = await asyncio.wait_for(ws.recv(), timeout=2.0)
            msg = json.loads(raw)
            if msg.get("type") == "ack" and msg.get("ref") == "a-audioe2e":
                ack = msg; break
        print(f"[2] audio:start ack: {ack}")
        assert ack is not None and ack.get("ok") is True, "audio:start failed"

        # 3) 推 UDP 包
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        flags = FLAG_FIRST | FLAG_LAST | FLAG_RAW_PCM
        payload = bytes(PAYLOAD_BYTES)   # 全零 PCM
        for i in range(N_FRAMES):
            frame = make_frame_v2(seq=i+1, pts=(i+1)*SAMPLES_PER_FRAME, flags=flags, payload=payload)
            sock.sendto(frame, ("127.0.0.1", host_port))
        print(f"[3] pushed {N_FRAMES} UDP frames to 127.0.0.1:{host_port}, payload {PAYLOAD_BYTES}B each")
        sock.close()

        # 4) 等一下让 Host 处理
        await asyncio.sleep(1.0)

        # 5) 关 ws
        await ws.close()
        print("[4] done")

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 51517
    asyncio.run(main(port))
