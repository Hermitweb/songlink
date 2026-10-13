#!/usr/bin/env python3
"""Same as audio-e2e.py but with small delay between UDP frames so C# UdpClient can keep up."""
import asyncio, json, socket, struct, sys, time

WS_URL = "ws://127.0.0.1:51999/"
PIN = "246810"
N_FRAMES = int(sys.argv[3]) if len(sys.argv) > 3 else 100
RATE = 48000
CH = 1
FRAME_MS = 20
SAMPLES_PER_FRAME = RATE * FRAME_MS // 1000   # 960
PAYLOAD_BYTES = SAMPLES_PER_FRAME * 2 * CH    # 1920
WIRE_VERSION = 1
FLAG_RAW_PCM = 0x04
FLAG_FIRST = 0x01
FLAG_LAST = 0x02

def make_frame(seq, pts, flags, payload):
    return bytes([WIRE_VERSION, flags]) \
        + struct.pack(">I", seq) \
        + struct.pack(">Q", pts) \
        + struct.pack(">H", len(payload)) \
        + payload

async def main(host_port: int):
    import websockets
    async with websockets.connect(WS_URL) as ws:
        hello = {"type":"hello","role":"remote","device":"slow","caps":["cmd"],"pin":PIN,"id":"h-slow","seq":1}
        await ws.send(json.dumps(hello))
        auth = json.loads(await ws.recv())
        print(f"[1] auth: ok={auth.get('ok')}")
        assert auth.get("ok") is True

        astart = {"type":"audio:start","id":"a-slow","rate":RATE,"ch":CH,"frameMs":FRAME_MS,"port":host_port,"seq":2}
        await ws.send(json.dumps(astart))
        ack = None
        for _ in range(10):
            raw = await asyncio.wait_for(ws.recv(), timeout=2.0)
            msg = json.loads(raw)
            if msg.get("type") == "ack" and msg.get("ref") == "a-slow":
                ack = msg; break
        print(f"[2] audio:start ack: {ack}")
        assert ack is not None and ack.get("ok") is True

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        flags = FLAG_FIRST | FLAG_LAST | FLAG_RAW_PCM
        payload = bytes(PAYLOAD_BYTES)
        sent = 0
        for i in range(N_FRAMES):
            frame = make_frame(seq=i+1, pts=(i+1)*SAMPLES_PER_FRAME, flags=flags, payload=payload)
            sock.sendto(frame, ("127.0.0.1", host_port))
            sent += 1
            if (i+1) % 10 == 0:
                await asyncio.sleep(0.01)   # 10ms pause every 10 frames
        print(f"[3] pushed {sent} UDP frames to 127.0.0.1:{host_port}")
        sock.close()
        await asyncio.sleep(2.0)
        await ws.close()

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 51000
    asyncio.run(main(port))
