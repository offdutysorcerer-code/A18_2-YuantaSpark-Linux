#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
WORK="$(mktemp -d /tmp/yuanta-market-rights-XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
cp "$HERE/Program.cs" "$HERE/MarketRightsProbe.csproj" "$WORK/"
docker run --rm \
  -v "$WORK:/work" \
  -v "$ROOT/vendor/yuanta:/vendor:ro" \
  -w /work \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet run --project MarketRightsProbe.csproj
