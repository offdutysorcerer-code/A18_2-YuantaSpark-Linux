#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$ROOT/data" "$ROOT/logs" "$ROOT/vendor/yuanta"
echo "Runtime directories ensured. Core project files are versioned directly; bootstrap does not generate them."
