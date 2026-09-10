#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
SYMBOL="${1:-2330}"
MARKET="${2:-TWSE}"
PORT="$(grep -E '^A18_HTTP_PORT=' .env 2>/dev/null | tail -1 | cut -d= -f2- || true)"
PORT="${PORT:-5220}"
BASE="http://127.0.0.1:${PORT}"
echo "[A18_2] subscribe ${MARKET}/${SYMBOL}"
curl -sS -i -X POST "$BASE/api/subscriptions/${MARKET}/${SYMBOL}"
echo -e "\n\n[A18_2] session"
curl -sS "$BASE/api/session"
echo -e "\n\n[A18_2] subscriptions"
curl -sS "$BASE/api/subscriptions"
echo -e "\n\n[A18_2] latest tick"
curl -sS -i "$BASE/api/ticks/${SYMBOL}/latest" || true
echo -e "\n\n[A18_2] ready"
curl -sS -i "$BASE/health/ready" || true
echo
