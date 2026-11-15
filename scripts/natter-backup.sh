#!/usr/bin/env bash
# Safe SQLite backup for Natter state. Usage: natter-backup.sh <db> <output>
set -euo pipefail
src="${1:?usage: natter-backup.sh <db> <output>}"
out="${2:?usage: natter-backup.sh <db> <output>}"
test -f "$src"
mkdir -p "$(dirname "$out")"
sqlite3 "$src" "PRAGMA wal_checkpoint(FULL);"
sqlite3 "$src" ".backup '$out'"
chmod 600 "$out"
echo "backup -> $out"
