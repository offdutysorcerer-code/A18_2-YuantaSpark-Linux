#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
PORT="$(grep -E '^A18_HTTP_PORT=' .env 2>/dev/null | tail -1 | cut -d= -f2- || true)"
PORT="${PORT:-5220}"
BASE="http://127.0.0.1:${PORT}"

echo "[A18_2] build + start"
docker compose up -d --build

echo "[A18_2] waiting for API..."
for _ in $(seq 1 45); do
  if curl -fsS "$BASE/health/live" >/dev/null 2>&1; then
    echo "[A18_2] API is live: $BASE"
    xdg-open "$BASE" >/dev/null 2>&1 &
    exit 0
  fi
  sleep 1
done

echo "[A18_2] API did not become live in 45 seconds." >&2
docker compose ps >&2
docker compose logs --tail=80 a18-realtime >&2
exit 1
