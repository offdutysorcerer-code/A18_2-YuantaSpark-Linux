#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
echo "[A18_2] restart"
docker compose restart
echo
docker compose ps
