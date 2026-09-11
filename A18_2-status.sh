#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
PORT="$(grep -E '^A18_HTTP_PORT=' .env 2>/dev/null | tail -1 | cut -d= -f2- || true)"
PORT="${PORT:-5220}"
BASE="http://127.0.0.1:${PORT}"

echo "[A18_2] docker"
docker compose ps

echo
echo "[A18_2] health/live"
curl -sS -i "$BASE/health/live" || true

echo -e "\n\n[A18_2] health/ready (channel-level live health)"
curl -sS -i "$BASE/health/ready" || true

echo -e "\n\n[A18_2] session"
curl -sS "$BASE/api/session" | python3 -m json.tool 2>/dev/null || true

echo -e "\n[A18_2] readiness (preopen universe / all desired symbols)"
curl -sS "$BASE/api/readiness" | python3 -m json.tool 2>/dev/null || true

echo -e "\n[A18_2] subscription summary"
curl -sS "$BASE/api/subscriptions" | python3 -c '
import json,sys
from collections import Counter
try:
    rows=json.load(sys.stdin)
except Exception:
    print("unavailable")
    raise SystemExit(0)
print("count=",len(rows))
print("states=",dict(Counter(x.get("state") for x in rows)))
print("backfill=",dict(Counter(x.get("backfillState") for x in rows)))
failed=[x for x in rows if x.get("state")=="FAILED" or x.get("backfillState")=="FAILED"]
if failed:
    print("failed=")
    for x in failed[:20]: print(x.get("market"),x.get("symbol"),x.get("lastError") or x.get("backfillError"))
' || true
