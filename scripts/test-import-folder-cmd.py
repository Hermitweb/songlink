"""Quick test for import.folder cmd."""
import asyncio, json, sys, websockets, os, time

async def main():
    # Get the PIN from the host log (we know it's running on 51999)
    # First, get current PIN by reading host log or using fixed one
    # The host is already running — find PIN
    # Simplest: use the PIN the host logged. We'll just hardcode latest one.
    # The Host started in the bg; look at process args
    PIN = sys.argv[1] if len(sys.argv) > 1 else None
    URL = "ws://127.0.0.1:51999/"

    if PIN is None:
        # Try to read the host log via cmd
        import subprocess
        r = subprocess.run(["powershell", "-NoProfile", "-Command",
            "Get-Process -Name SongLink.Host -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $_.Id }"],
            capture_output=True, text=True)
        print("No PIN supplied. Use: python test-import-folder-cmd.py <PIN>")
        return 1

    async with websockets.connect(URL) as ws:
        await ws.send(json.dumps({
            "v":1,"seq":1,"type":"hello","id":"py-h","role":"remote",
            "device":"py-import-test","os":"win32","caps":["cmd"],"pin":PIN
        }))
        for _ in range(5):
            msg = await asyncio.wait_for(ws.recv(), timeout=1.0)
            f = json.loads(msg)
            if f.get("type") == "authResult":
                if not f.get("ok"):
                    print(f"[FAIL] auth: {f}")
                    return 1
                break

        # Use a path that exists on the host (the local wwwroot or test dir)
        # For a more meaningful test, scan the published exe directory
        test_path = sys.argv[2] if len(sys.argv) > 2 else os.getcwd()
        print(f"[INFO] scanning {test_path}")
        await ws.send(json.dumps({
            "v":1,"seq":2,"type":"cmd","id":"py-imp1",
            "action":"import.folder","args":{"path":test_path}
        }))
        deadline = asyncio.get_event_loop().time() + 15
        saw_ack = saw_result = False
        while asyncio.get_event_loop().time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=0.5)
                f = json.loads(msg)
                if f.get("type") == "ack" and f.get("ref") == "py-imp1":
                    saw_ack = True
                    print(f"[{'OK' if f.get('ok') else 'FAIL'}] ack ok={f.get('ok')}")
                if f.get("type") == "event" and f.get("name") == "import-result":
                    saw_result = True
                    r = (f.get("queue") or [{}])[0]
                    print(f"[OK] import-result: total={r.get('total')}, imported={r.get('imported')}, failed={r.get('failed')}")
                    break
            except asyncio.TimeoutError:
                if saw_ack and not saw_result:
                    continue
                break

        return 0 if (saw_ack and saw_result) else 1

print("OK" if asyncio.run(main()) == 0 else "FAIL")
