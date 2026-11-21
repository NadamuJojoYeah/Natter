#!/usr/bin/env bash
# Measures the egress path to Discord and says whether it can meet the 3-second interaction ack.
#
# Discord requires an interaction (slash command) to be acknowledged within 3s. Natter defers
# immediately on receipt, but the defer itself is an HTTPS POST, so the *round trip* decides
# whether /ask /memory /remember work. A link that succeeds in 5s still loses.
#
# The probe is unauthenticated on purpose: it measures DNS + TLS + HTTP RTT to discord.com and
# gateway.discord.gg without needing the bot token.
#
# Usage:
#   ./scripts/discord-egress-probe.sh                      # 6 samples, current env
#   ./scripts/discord-egress-probe.sh -n 10 -i 2           # 10 samples, 2s apart
#   ./scripts/discord-egress-probe.sh --proxy http://127.0.0.1:7890 -n 6
#   ./scripts/discord-egress-probe.sh --direct --json      # ignore proxy env vars
#
# Exit codes: 0 = budget met (p95 < 2000ms, success >= 90%), 1 = budget at risk, 2 = usage,
#             3 = no network at all (all samples failed).
set -uo pipefail

SAMPLES=6
INTERVAL=2
PROXY=""
MODE=""           # "", "direct"
OUT=""
JSON=0
# Budget: 3s hard limit, keep 1s for processing/persist/send.
BUDGET_MS=2000
MIN_SUCCESS_PERCENT=90

usage() {
  sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -n|--samples) SAMPLES="${2:-}"; shift 2 ;;
    -i|--interval) INTERVAL="${2:-}"; shift 2 ;;
    --proxy) PROXY="${2:-}"; shift 2 ;;
    --direct) MODE="direct"; shift ;;
    -o|--output) OUT="${2:-}"; shift 2 ;;
    --json) JSON=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

[[ "$SAMPLES" =~ ^[0-9]+$ ]] && (( SAMPLES > 0 )) || { echo "invalid sample count" >&2; exit 2; }

if [[ -n "$PROXY" ]]; then
  export HTTP_PROXY="$PROXY" HTTPS_PROXY="$PROXY" ALL_PROXY="$PROXY"
  export http_proxy="$PROXY" https_proxy="$PROXY" all_proxy="$PROXY"
elif [[ "$MODE" == "direct" ]]; then
  unset HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy
fi

declare -a results=()
declare -a codes=()

probe() {
  local url="$1" label="$2"
  local out
  out="$(curl -sS -o /dev/null --max-time 10 \
      -w '%{http_code} %{time_namelookup} %{time_connect} %{time_appconnect} %{time_total}' \
      "$url" 2>/dev/null)" || out="000 0 0 0 0"
  local code nl ap total
  read -r code nl _connect ap total <<<"$out"
  local total_ms
  total_ms="$(awk -v t="$total" 'BEGIN{printf "%d", t*1000}')"
  results+=("$total_ms")
  codes+=("$code")
  [[ "$JSON" == "1" ]] || printf '  %-28s http=%s dns=%sms tls=%sms total=%sms\n' \
    "$label" "$code" \
    "$(awk -v t="$nl" 'BEGIN{printf "%d", t*1000}')" \
    "$(awk -v t="$ap" 'BEGIN{printf "%d", t*1000}')" "$total_ms"
}

if [[ "$JSON" != "1" ]]; then
  echo "== Discord egress probe (samples=$SAMPLES) =="
  if [[ -n "$PROXY" ]]; then
    echo "proxy: $PROXY"
  elif [[ "$MODE" == "direct" ]]; then
    echo "egress: direct (proxy env vars unset)"
  else
    echo "egress: current environment"
    env | grep -iE '^(http_proxy|https_proxy|all_proxy)=' | sed 's/=.*/=<set>/' || echo "  no proxy variables set (direct)"
  fi
fi

for ((i = 1; i <= SAMPLES; i++)); do
  probe "https://discord.com/api/v10/gateway" "discord.com api/v10/gateway"
  probe "https://gateway.discord.gg/?v=10&encoding=json" "gateway.discord.gg handshake"
  [[ "$i" -lt "$SAMPLES" ]] && sleep "$INTERVAL"
done

# Aggregate: p95 (nearest-rank) and success ratio over 200/204 responses.
sorted="$(printf '%s\n' "${results[@]}" | sort -n)"
count="${#results[@]}"
p95_idx=$(( (count * 95 + 99) / 100 ))
p95="$(printf '%s\n' "$sorted" | sed -n "${p95_idx}p")"
p50_idx=$(( (count + 1) / 2 ))
p50="$(printf '%s\n' "$sorted" | sed -n "${p50_idx}p")"
max="$(printf '%s\n' "$sorted" | tail -1)"
min="$(printf '%s\n' "$sorted" | head -1)"

# Any HTTP response proves DNS+TLS+RTT worked; 401/404 are expected without a token, and only a
# transport failure (curl exit != 0 -> code 000) counts as a lost sample.
success=0
for code in "${codes[@]}"; do
  [[ "$code" != "000" ]] && success=$((success + 1))
done
success_pct=$(( success * 100 / count ))

# Network-latency component only: DNS/connect/TLS are excluded, the 3s budget is about RTT.
verdict="PASS"
reasons=()
if [[ "$success_pct" -lt "$MIN_SUCCESS_PERCENT" ]]; then
  verdict="FAIL"
  reasons+=("success ${success_pct}% < ${MIN_SUCCESS_PERCENT}%")
fi
if [[ "$p95" -gt "$BUDGET_MS" ]]; then
  verdict="FAIL"
  reasons+=("p95 ${p95}ms exceeds the ${BUDGET_MS}ms budget (3s ack minus processing)")
fi
if [[ "$success" -eq 0 ]]; then
  verdict="NO-NETWORK"
fi

summary="$(printf '{"samples":%d,"successPercent":%d,"minMs":%d,"p50Ms":%d,"p95Ms":%d,"maxMs":%d,"budgetMs":%d,"verdict":"%s"}' \
  "$count" "$success_pct" "$min" "$p50" "$p95" "$max" "$BUDGET_MS" "$verdict")"

if [[ -n "$OUT" ]]; then
  mkdir -p "$(dirname "$OUT")"
  {
    printf '%s\n' "$summary"
    printf 'codes: %s\n' "${codes[*]}"
    printf 'samplesMs: %s\n' "${results[*]}"
  } >"$OUT"
fi

if [[ "$JSON" == "1" ]]; then
  printf '%s\n' "$summary"
else
  printf '\n  min=%sms p50=%sms p95=%sms max=%sms success=%d/%d (%d%%)\n' \
    "$min" "$p50" "$p95" "$max" "$success" "$count" "$success_pct"
  printf '  verdict: %s\n' "$verdict"
  for reason in "${reasons[@]:-}"; do
    [[ -n "$reason" ]] && printf '  - %s\n' "$reason"
  done
  cat <<'EOF'

  read it as:
    p95 < 1000ms  : comfortable, interaction acks land well inside the 3s budget
    1000-2000ms   : usable but tight; a single spike breaks /ask intermittently
    > 2000ms      : defer will regularly exceed 3s -> "该应用程序未响应"
  See docs/operations/discord-3s-interaction-egress.md for the fix options.
EOF
fi

case "$verdict" in
  PASS) exit 0 ;;
  NO-NETWORK) exit 3 ;;
  *) exit 1 ;;
esac
