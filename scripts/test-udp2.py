#!/usr/bin/env python3
"""Send N frames with logging."""
import socket, struct, time, sys

N = int(sys.argv[1]) if len(sys.argv) > 1 else 5
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 51001

payload = b'\xaa' * 1920
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
for i in range(N):
    seq = i + 1
    pts = (i + 1) * 960
    flags = 0x07
    frame = bytes([1, flags]) + struct.pack(">I", seq) + struct.pack(">Q", pts) + struct.pack(">H", len(payload)) + payload
    sent = sock.sendto(frame, ("127.0.0.1", PORT))
    print(f"[{i+1}/{N}] sent {sent}B seq={seq}", flush=True)
    time.sleep(0.05)
sock.close()
print(f"Done: sent {N} frames to 127.0.0.1:{PORT}")
