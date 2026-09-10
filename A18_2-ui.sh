#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
PORT="$(grep -E '^A18_HTTP_PORT=' .env 2>/dev/null | tail -1 | cut -d= -f2- || true)"
PORT="${PORT:-5220}"
URL="http://127.0.0.1:${PORT}"
echo "[A18_2] open ${URL}"
xdg-open "$URL" >/dev/null 2>&1 &
