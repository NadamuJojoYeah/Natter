#!/usr/bin/env bash
# Publishes the Natter CLI as a self-contained single-file executable per runtime.
# Usage: ./scripts/publish.sh [rid ...]
# Example: ./scripts/publish.sh linux-x64 osx-arm64 win-x64
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_ROOT="${OUTPUT_ROOT:-$ROOT/artifacts/cli}"

if [ "$#" -gt 0 ]; then
  RIDS=("$@")
else
  RIDS=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64)
fi

for rid in "${RIDS[@]}"; do
  echo "==> publishing Natter CLI for $rid ($CONFIGURATION)"
  dotnet publish "$ROOT/src/Natter.Cli/Natter.Cli.csproj" \
    -c "$CONFIGURATION" \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=embedded \
    -o "$OUTPUT_ROOT/$rid"
done

echo
echo "Published to $OUTPUT_ROOT"
