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
echo -e "\n\n[A18_2] health/ready"
curl -sS -i "$BASE/health/ready" || true
echo -e "\n\n[A18_2] session"
curl -sS "$BASE/api/session" || true
echo -e "\n\n[A18_2] subscriptions"
curl -sS "$BASE/api/subscriptions" || true
echo
