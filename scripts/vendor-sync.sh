#!/usr/bin/env bash
set -euo pipefail

SOURCE="${YUANTA_SDK_SOURCE:-}"
if [[ -z "$SOURCE" ]]; then
  echo "Set YUANTA_SDK_SOURCE to a directory containing YuantaSparkAPI.dll and runtimes/." >&2
  exit 2
fi
if [[ ! -f "$SOURCE/YuantaSparkAPI.dll" ]]; then
  echo "YuantaSparkAPI.dll not found in: $SOURCE" >&2
  exit 3
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/vendor/yuanta"
mkdir -p "$DEST"
find "$DEST" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp "$SOURCE"/*.dll "$DEST"/
if [[ -d "$SOURCE/runtimes" ]]; then
  cp -a "$SOURCE/runtimes" "$DEST/"
fi
rm -f "$DEST/A18_2.dll"
echo "Yuanta runtime staged at $DEST"
