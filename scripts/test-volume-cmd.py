"""Quick test: connect to host, send volume cmd, verify ack + volume-changed event."""
import asyncio, json, sys
import websockets

PIN = "475295"
URL = "ws://127.0.0.1:51999/"

async def main():
    async with websockets.connect(URL, subprotocols=[]) as ws:
        # Hello + PIN
        await ws.send(json.dumps({
            "v": 1, "seq": 1, "type": "hello",
            "id": "py-h", "role": "remote", "device": "py-volume-test",
            "os": "win32", "caps": ["cmd"], "pin": PIN,
        }))
        frames = []
        for _ in range(5):
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=1.0)
                frames.append(json.loads(msg))
                if any(f.get("type") == "authResult" for f in frames):
                    break
            except asyncio.TimeoutError:
                break
        auth = next((f for f in frames if f.get("type") == "authResult"), None)
        if not auth or not auth.get("ok"):
            print("[FAIL] auth:", auth); return 1
        print(f"[OK] auth token={auth.get('token','')[:8]}...")

        # 收尾剩余(Hello/State)
        for _ in range(3):
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.3)
                frames.append(json.loads(msg))
            except asyncio.TimeoutError:
                break

        # 发 volume cmd level=0.42
        await ws.send(json.dumps({
            "v": 1, "seq": 2, "type": "cmd", "id": "py-v1",
            "action": "volume", "args": {"level": 0.42}
        }))
        # 收 Ack + volume-changed event + State
        saw_ack = saw_evt = saw_state = False
        state_vol = None
        deadline = asyncio.get_event_loop().time() + 3
        while asyncio.get_event_loop().time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.5)
                f = json.loads(msg)
                if f.get("type") == "ack" and f.get("ref") == "py-v1":
                    saw_ack = True
                    print(f"[OK] ack ok={f.get('ok')}")
                if f.get("type") == "event" and f.get("name") == "volume-changed":
                    saw_evt = True
                    print("[OK] event volume-changed received")
                if f.get("type") == "state" and f.get("now"):
                    saw_state = True
                    state_vol = f["now"].get("volume")
                    print(f"[OK] state.volume={state_vol}")
                if saw_ack and saw_evt and saw_state:
                    break
            except asyncio.TimeoutError:
                break

        # 再发 level=5.0 (clamp)
        await ws.send(json.dumps({
            "v": 1, "seq": 3, "type": "cmd", "id": "py-v2",
            "action": "volume", "args": {"level": 5.0}
        }))
        deadline = asyncio.get_event_loop().time() + 2
        while asyncio.get_event_loop().time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.5)
                f = json.loads(msg)
                if f.get("type") == "ack" and f.get("ref") == "py-v2":
                    print(f"[OK] clamp ack ok={f.get('ok')}")
                if f.get("type") == "state" and f.get("now", {}).get("volume") == 1.0:
                    print("[OK] clamp state.volume=1.0")
                    break
            except asyncio.TimeoutError:
                break

        # 再发 level=-1 (clamp to 0)
        await ws.send(json.dumps({
            "v": 1, "seq": 4, "type": "cmd", "id": "py-v3",
            "action": "volume", "args": {"level": -1}
        }))
        deadline = asyncio.get_event_loop().time() + 2
        while asyncio.get_event_loop().time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.5)
                f = json.loads(msg)
                if f.get("type") == "ack" and f.get("ref") == "py-v3":
                    print(f"[OK] mute ack ok={f.get('ok')}")
                if f.get("type") == "state" and f.get("now", {}).get("volume") == 0.0:
                    print("[OK] mute state.volume=0.0")
                    break
            except asyncio.TimeoutError:
                break

        # 缺 level → 应回 ack ok=false
        await ws.send(json.dumps({
            "v": 1, "seq": 5, "type": "cmd", "id": "py-v4", "action": "volume"
        }))
        deadline = asyncio.get_event_loop().time() + 2
        while asyncio.get_event_loop().time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.5)
                f = json.loads(msg)
                if f.get("type") == "ack" and f.get("ref") == "py-v4":
                    print(f"[{'OK' if f.get('ok') == False else 'FAIL'}] missing-arg ack ok={f.get('ok')}")
                    return 0 if f.get('ok') == False else 1
            except asyncio.TimeoutError:
                break
        print("[FAIL] missing-arg ack never received"); return 1

print("OK" if asyncio.run(main()) == 0 else "FAIL")
