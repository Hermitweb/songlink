#!/usr/bin/env python3
"""Send 1 UDP frame to test."""
import asyncio, json, socket, struct, sys, websockets

WS_URL = "ws://127.0.0.1:51999/"
PIN = "246810"
WIRE_VERSION = 1
FLAG_RAW_PCM = 0x04
FLAG_FIRST = 0x01
FLAG_LAST = 0x02

def make_frame(seq, pts, flags, payload):
    h = bytes([WIRE_VERSION, flags]) \
        + struct.pack(">I", seq) \
        + struct.pack(">Q", pts) \
        + struct.pack(">H", len(payload))
    return h + payload

async def main():
    async with websockets.connect(WS_URL) as ws:
        await ws.send(json.dumps({"type":"hello","role":"remote","device":"1f","caps":["cmd"],"pin":PIN,"id":"h1f","seq":1}))
        await asyncio.wait_for(ws.recv(), timeout=2.0)  # authResult
        await ws.send(json.dumps({"type":"audio:start","id":"a1f","rate":48000,"ch":1,"frameMs":20,"port":51000,"seq":2}))
        await asyncio.wait_for(ws.recv(), timeout=2.0)  # ack

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        payload = bytes(1920)
        f = make_frame(1, 960, FLAG_FIRST|FLAG_LAST|FLAG_RAW_PCM, payload)
        print(f"frame len={len(f)}, header hex={f[:16].hex()}")
        sock.sendto(f, ("127.0.0.1", 51000))
        print("Sent 1 frame")
        sock.close()
        await asyncio.sleep(1.0)
        await ws.close()

asyncio.run(main())
