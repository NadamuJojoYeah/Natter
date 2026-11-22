#!/usr/bin/env bash
# Passive health sampler for a long-running Natter host (24h soak, post-deploy watch, incident).
#
# It only reads the loopback /health endpoint, so it never competes with the running host for the
# Discord gateway session (see OPERATIONS_RUNBOOK §5.3). One JSON object per line.
#
# Usage:
#   ./scripts/soak-sample.sh                          # 300s interval, 24h, default output
#   ./scripts/soak-sample.sh -i 60 -d 3600            # 1h watch, 60s interval
#   ./scripts/soak-sample.sh -o /var/log/natter/soak-2026-11-21.jsonl -i 300 -d 86400
#   ./scripts/soak-sample.sh -i 300 -d 86400 --offline-fallback   # no host running: CLI fallback
#
# Output lines:
#   {"sampledAt":"...Z","sampledWallClock":"...+08:00","source":"loopback","status":"healthy",...}
#   {"sampledAt":"...Z","source":"loopback","error":"unreachable","httpCode":"000"}
#
# Best-effort extras (omitted when unavailable, never fail a sample):
#   bootId   /proc/sys/kernel/random/boot_id — lets soak-report --mode cumulative attribute gaps
#            to a host power cycle vs a service restart.
#   rssBytes systemctl show $NATTER_UNIT -p MemoryCurrent, else /proc/<MainPID>/status VmRSS — feeds
#            the cumulative-mode memory-drift check.
#   Override the unit with NATTER_UNIT (default: natter.service).
#
# Exit codes: 0 = loop finished, 2 = bad usage, 3 = output path not writable.
set -euo pipefail

PORT="${NATTER_HEALTH_PORT:-5099}"
INTERVAL=300
DURATION=86400
OUT="${SOAK_OUT:-}"
OFFLINE_FALLBACK=0
NATTER_BIN="${NATTER_BIN:-natter}"
OUT_DIR=""
ONCE=0
QUIET=0

usage() {
  sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -i|--interval) INTERVAL="${2:-}"; shift 2 ;;
    -d|--duration) DURATION="${2:-}"; shift 2 ;;
    -o|--output) OUT="${2:-}"; shift 2 ;;
    -p|--port) PORT="${2:-}"; shift 2 ;;
    --offline-fallback) OFFLINE_FALLBACK=1; shift ;;
    -1|--once) ONCE=1; shift ;;
    --out-dir) OUT_DIR="${2:-}"; shift 2 ;;
    -q|--quiet) QUIET=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
done

[[ "$INTERVAL" =~ ^[0-9]+$ ]] && (( INTERVAL > 0 )) || { echo "invalid interval: $INTERVAL" >&2; exit 2; }
[[ "$DURATION" =~ ^[0-9]+$ ]] || { echo "invalid duration: $DURATION" >&2; exit 2; }

if [[ -n "$OUT_DIR" ]]; then
  OUT="$OUT_DIR/soak-$(date +%F).jsonl"
fi
if [[ -z "$OUT" ]]; then
  OUT="${NATTER_STATE_DIR:-/var/lib/natter}/log/soak-$(date +%F).jsonl"
fi
# A timer-driven sampler appends to the same daily file: only the first writer records the contract.
FIRST_WRITE=0
[[ -f "$OUT" ]] || FIRST_WRITE=1
mkdir -p "$(dirname "$OUT")" 2>/dev/null || { echo "cannot create $(dirname "$OUT")" >&2; exit 3; }
[[ -w "$(dirname "$OUT")" ]] || { echo "not writable: $(dirname "$OUT")" >&2; exit 3; }
chmod 600 "$OUT" 2>/dev/null || true

# Best-effort: empty output means "omit the field", never an error.
collect_boot_id() {
  local boot=""
  if [[ -r /proc/sys/kernel/random/boot_id ]]; then
    boot="$(head -n 1 /proc/sys/kernel/random/boot_id 2>/dev/null | tr -d '[:space:]')"
  fi
  printf '%s' "$boot"
}

collect_rss_bytes() {
  local unit="${NATTER_UNIT:-natter.service}"
  local value="" pid="" kb=""

  if command -v systemctl >/dev/null 2>&1; then
    value="$(systemctl show "$unit" -p MemoryCurrent --value 2>/dev/null | tr -d '[:space:]')"
    # systemd prints "[not set]" when the accounting counter is unavailable.
    [[ "$value" =~ ^[0-9]+$ ]] || value=""
  fi

  if [[ -z "$value" ]] && command -v systemctl >/dev/null 2>&1; then
    pid="$(systemctl show "$unit" -p MainPID --value 2>/dev/null | tr -d '[:space:]')"
    [[ "$pid" =~ ^[0-9]+$ ]] || pid=""
    [[ "$pid" == "0" ]] && pid=""
  fi

  # Fallback for non-systemd deployments: newest natter serve process of this user.
  if [[ -z "$value" && -z "$pid" ]] && command -v pgrep >/dev/null 2>&1; then
    pid="$(pgrep -n -f 'natter.*serve' 2>/dev/null || true)"
  fi

  if [[ -z "$value" && -n "$pid" && -r "/proc/$pid/status" ]]; then
    kb="$(awk '/^VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null)"
    if [[ "$kb" =~ ^[0-9]+$ ]]; then
      value="$(( kb * 1024 ))"
    fi
  fi

  printf '%s' "$value"
}

iso_utc() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }
iso_local() { date +%Y-%m-%dT%H:%M:%S%:z; }
log() { [[ "$QUIET" == "1" ]] || echo "$*" >&2; }

end_epoch=$(( $(date +%s) + DURATION ))
start_epoch=$(date +%s)

# Header line: records the sampling contract so a later audit can recompute expected coverage.
if [[ "$FIRST_WRITE" == "1" ]]; then
  printf '{"soakStart":"%s","soakStartWallClock":"%s","intervalSeconds":%s,"durationSeconds":%s,"port":%s,"pid":%s,"mode":"%s"}\n' \
    "$(iso_utc)" "$(iso_local)" "$INTERVAL" "$DURATION" "$PORT" "$$" \
    "$([[ "$ONCE" == "1" ]] && echo timer || echo loop)" >>"$OUT"
fi
log "soak sampling -> $OUT (interval=${INTERVAL}s duration=${DURATION}s port=$PORT once=$ONCE)"

stop_requested=0
cleanup() {
  stop_requested=1
  local elapsed=$(( $(date +%s) - start_epoch ))
  printf '{"soakStop":"%s","soakStopWallClock":"%s","elapsedSeconds":%s,"reason":"signal"}\n' \
    "$(iso_utc)" "$(iso_local)" "$elapsed" >>"$OUT"
  log "soak sampler stopped after ${elapsed}s"
}
trap cleanup TERM INT

while [[ "$stop_requested" == "0" ]]; do
  now_epoch=$(date +%s)
  (( now_epoch < end_epoch )) || break

  sampled_utc="$(iso_utc)"
  sampled_local="$(iso_local)"
  boot_id="$(collect_boot_id)"
  rss_bytes="$(collect_rss_bytes)"
  boot_suffix=""
  if [[ -n "$boot_id" ]]; then
    boot_suffix=",\"bootId\":\"$boot_id\""
  fi

  body=""
  http_code="000"
  response="$(curl -sS --max-time 5 -w '\n%{http_code}' "http://127.0.0.1:${PORT}/health" 2>/dev/null || true)"
  if [[ -n "$response" ]]; then
    http_code="${response##*$'\n'}"
    body="${response%$'\n'*}"
  fi

  if [[ "$http_code" == "200" && -n "$body" ]]; then
    if command -v jq >/dev/null 2>&1; then
      printf '%s\n' "$body" | jq -c \
        --arg ts "$sampled_utc" --arg wall "$sampled_local" \
        --arg boot "$boot_id" --arg rss "$rss_bytes" \
        '. + {sampledAt:$ts, sampledWallClock:$wall, source:"loopback"}
           + (if $boot != "" then {bootId:$boot} else {} end)
           + (if $rss != "" then {rssBytes:($rss | tonumber)} else {} end)' >>"$OUT" 2>/dev/null \
        || printf '{"sampledAt":"%s","sampledWallClock":"%s","source":"loopback","error":"parse","bootId":"%s"}\n' \
             "$sampled_utc" "$sampled_local" "$boot_id" >>"$OUT"
    else
      # No jq: keep the raw body but wrap the sample metadata as a separate prefix-free line.
      printf '%s\n' "$body" >>"$OUT"
    fi
  elif [[ "$OFFLINE_FALLBACK" == "1" ]]; then
    if offline="$("$NATTER_BIN" health --offline --json 2>/dev/null)" && [[ -n "$offline" ]]; then
      if command -v jq >/dev/null 2>&1; then
        printf '%s\n' "$offline" | jq -c \
          --arg ts "$sampled_utc" --arg wall "$sampled_local" \
          --arg boot "$boot_id" --arg rss "$rss_bytes" \
          '. + {sampledAt:$ts, sampledWallClock:$wall, source:"offline"}
             + (if $boot != "" then {bootId:$boot} else {} end)
             + (if $rss != "" then {rssBytes:($rss | tonumber)} else {} end)' >>"$OUT" 2>/dev/null || true
      fi
    else
      printf '{"sampledAt":"%s","sampledWallClock":"%s","source":"offline","error":"unreachable","httpCode":"%s"%s}\n' \
        "$sampled_utc" "$sampled_local" "$http_code" "${boot_suffix}" >>"$OUT"
    fi
  else
    printf '{"sampledAt":"%s","sampledWallClock":"%s","source":"loopback","error":"unreachable","httpCode":"%s"%s}\n' \
      "$sampled_utc" "$sampled_local" "$http_code" "${boot_suffix}" >>"$OUT"
  fi

  if [[ "$ONCE" == "1" ]]; then
    exit 0
  fi
  sleep "$INTERVAL" &
  wait $! 2>/dev/null || true
done

elapsed=$(( $(date +%s) - start_epoch ))
if [[ "$ONCE" != "1" ]]; then
  printf '{"soakStop":"%s","soakStopWallClock":"%s","elapsedSeconds":%s,"reason":"duration"}\n' \
    "$(iso_utc)" "$(iso_local)" "$elapsed" >>"$OUT"
  log "soak finished: ${elapsed}s of ${DURATION}s, $(grep -c '' "$OUT") lines in $OUT"
fi
