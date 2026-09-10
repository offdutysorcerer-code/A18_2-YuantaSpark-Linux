#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
echo "[A18_2] Ctrl+C to stop following logs"
docker compose logs -f --tail=120 a18-realtime
