#!/usr/bin/env bash
# P2 SIGINT/SIGTERM smoke for the non-TTY serve path.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI="$ROOT/src/Natter.Cli/bin/Debug/net10.0/natter"
OUT="$ROOT/artifacts/p2-signal"
mkdir -p "$OUT"

check() {
  local signal="$1"
  local log="$OUT/$signal.log"
  "$CLI" serve --no-plugins --duration 0 >"$log" 2>&1 &
  local pid=$!
  sleep 1
  kill "-$signal" "$pid"
  for _ in $(seq 1 50); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
  if kill -0 "$pid" 2>/dev/null; then
    echo "FAIL: $signal did not stop within 5s" >&2
    kill -KILL "$pid" 2>/dev/null || true
    return 1
  fi
  wait "$pid"
  local status=$?
  if [[ "$status" -ne 0 ]]; then
    echo "FAIL: $signal exit=$status" >&2
    return 1
  fi
  echo "PASS: $signal exit=0"
}

check INT
check TERM
