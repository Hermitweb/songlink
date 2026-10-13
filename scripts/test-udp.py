#!/usr/bin/env python3
"""Send 1 frame to 127.0.0.1:51001 (PS listener) to verify Python UDP works."""
import socket, struct

payload = b'\x00' * 1920
seq = 1
pts = 960
flags = 0x07
frame = bytes([1, flags]) + struct.pack(">I", seq) + struct.pack(">Q", pts) + struct.pack(">H", len(payload)) + payload

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 65536)
sent = sock.sendto(frame, ("127.0.0.1", 51001))
print(f"Sent {sent} bytes to 127.0.0.1:51001")
sock.close()
