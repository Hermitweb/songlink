#!/usr/bin/env bash
# SongLink HMAC 签名端到端联调脚本（任务3）
# 起一个开启强制签名的 Host，用 RemoteSim --sign --verify 连接，验证：
#   入站命令带签名被接受(ack ok)、出站广播帧带 hostKey 签名且客户端校验通过(sig=✓)。
# 用法： bash scripts/sig-smoke.sh
set -u
cd "$(dirname "$0")/.."
PORT="${PORT:-52333}"
LOG="$(mktemp)"; OUT="$(mktemp)"
trap 'powershell.exe -NoProfile -Command "Get-Process SongLink.Host -EA SilentlyContinue | Stop-Process -Force" >/dev/null 2>&1; rm -f "$LOG" "$OUT"' EXIT

echo "[smoke] 启动 Host（requireFrameSig=true, port=$PORT）"
dotnet run --project src/SongLink.Host.Core -c Debug -- run --port "$PORT" --require-sig true > "$LOG" 2>&1 &
for i in $(seq 1 40); do grep -q "PIN=" "$LOG" && break; sleep 0.3; done
PIN=$(grep -oE "PIN=[0-9]{6}" "$LOG" | head -1 | cut -d= -f2)
if [ -z "$PIN" ]; then echo "[smoke] ✗ Host 未起来"; cat "$LOG"; exit 1; fi
echo "[smoke] Host PIN=$PIN，运行 RemoteSim --sign --verify"

dotnet run --project src/SongLink.RemoteSim -c Debug --no-build -- \
  --url "ws://localhost:$PORT/" --pin "$PIN" --sign --verify \
  --cmds "next,search:Coldplay,pick:Yellow:Coldplay" --seconds 2 2>&1 | tee "$OUT"

echo "[smoke] 断言…"
fail=0
grep -q "authResult ok=True" "$OUT" || { echo "  ✗ 未鉴权成功"; fail=1; }
grep -q "hostKey=已下发" "$OUT" || { echo "  ✗ authResult 未带 hostKey"; fail=1; }
grep -q "sig=✓" "$OUT" || { echo "  ✗ 未见出站帧签名校验通过(sig=✓)"; fail=1; }
grep -q "sig=✗" "$OUT" && { echo "  ✗ 出现签名校验失败(sig=✗)"; fail=1; }
grep -q "ack ref=.* ok=True" "$OUT" || { echo "  ✗ 签名入站命令未获 ack ok"; fail=1; }
grep -q "AUTH_FAILED" "$OUT" && { echo "  ✗ 出现 AUTH_FAILED（签名应被接受）"; fail=1; }

if [ "$fail" = 0 ]; then echo "[smoke] ✅ HMAC 签名端到端联调通过"; exit 0; else echo "[smoke] ❌ 失败"; exit 1; fi
