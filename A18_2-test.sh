#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
SYMBOL="${1:-2330}"
MARKET="${2:-TWSE}"
DATE="${3:-$(TZ=Asia/Taipei date +%F)}"
PORT="$(grep -E '^A18_HTTP_PORT=' .env 2>/dev/null | tail -1 | cut -d= -f2- || true)"
PORT="${PORT:-5220}"
BASE="http://127.0.0.1:${PORT}"

echo "[A18_2] subscribe ${MARKET}/${SYMBOL}"
curl -sS -i -X POST "$BASE/api/subscriptions/${MARKET}/${SYMBOL}"

echo -e "\n\n[A18_2] per-symbol readiness"
curl -sS "$BASE/api/readiness?symbols=${SYMBOL}" | python3 -m json.tool

echo -e "\n[A18_2] explicit trade-date data (${DATE}; no fallback allowed)"
curl -sS "$BASE/api/ticks/${SYMBOL}?date=${DATE}" | python3 -m json.tool

echo -e "\n[A18_2] latest live tick (today only)"
curl -sS -i "$BASE/api/ticks/${SYMBOL}/latest" || true

echo -e "\n\n[A18_2] channel health/ready"
curl -sS -i "$BASE/health/ready" || true
echo
