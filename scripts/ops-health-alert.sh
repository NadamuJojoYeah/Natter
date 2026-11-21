#!/usr/bin/env bash
# Health/alert check for a running Natter host — the operational half of OBS-019.
#
# The host itself does not tell you *why* a Discord gateway connection dropped: `OnReady` clears
# `LastError` on every successful reconnect, so after a short blip you only see `reconnectCount`
# climbing and `lastError: null`. This script keeps the previous snapshot and turns the observable
# signals into explicit alerts, so a drop is at least attributed to "disconnect with no reason
# recorded (OBS-019)" instead of silently disappearing.
#
# It changes nothing on the host and never starts a second gateway session: it reads the loopback
# /health endpoint, or a snapshot file when no host is running.
#
# Usage:
#   ./scripts/ops-health-alert.sh                                  # loopback :5099, default state
#   ./scripts/ops-health-alert.sh -p 5099 --state /var/lib/natter/log/alert-state.json
#   ./scripts/ops-health-alert.sh --file artifacts/.../health.json
#   ./scripts/ops-health-alert.sh --json                           # machine-readable report
#
# Exit codes: 0 = no alert, 1 = alert raised, 2 = usage error, 3 = no snapshot available.
set -euo pipefail

PORT="${NATTER_HEALTH_PORT:-5099}"
STATE_FILE="${ALERT_STATE_FILE:-${NATTER_STATE_DIR:-/var/lib/natter}/log/alert-state.json}"
SNAPSHOT_FILE=""
JSON=0
# A drop that is followed by a reconnect leaves only the counter. Alert on any increase.
RECONNECT_ALERT=${RECONNECT_ALERT:-1}
# Discord interaction ack budget (3s) failures surface as this error prefix.
INTERACTION_ERROR_PREFIX="interaction_defer_failed"

usage() {
  sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -p|--port) PORT="${2:-}"; shift 2 ;;
    --state) STATE_FILE="${2:-}"; shift 2 ;;
    --file) SNAPSHOT_FILE="${2:-}"; shift 2 ;;
    --json) JSON=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
done

require() {
  command -v "$1" >/dev/null 2>&1 || { echo "missing dependency: $1" >&2; exit 2; }
}
require jq

snapshot=""
if [[ -n "$SNAPSHOT_FILE" ]]; then
  [[ -r "$SNAPSHOT_FILE" ]] || { echo "cannot read snapshot: $SNAPSHOT_FILE" >&2; exit 3; }
  snapshot="$(cat "$SNAPSHOT_FILE")"
else
  snapshot="$(curl -sS --max-time 5 "http://127.0.0.1:${PORT}/health" 2>/dev/null || true)"
fi
if [[ -z "${snapshot//[[:space:]]/}" ]]; then
  echo "no health snapshot (host not running on 127.0.0.1:${PORT} and no --file given)" >&2
  exit 3
fi
if ! printf '%s' "$snapshot" | jq -e . >/dev/null 2>&1; then
  echo "health snapshot is not valid JSON" >&2
  exit 3
fi

prev="{}"
if [[ -r "$STATE_FILE" ]]; then
  prev="$(cat "$STATE_FILE" 2>/dev/null || echo '{}')"
  printf '%s' "$prev" | jq -e . >/dev/null 2>&1 || prev="{}"
fi

mkdir -p "$(dirname "$STATE_FILE")" 2>/dev/null || true

report="$(jq -cn \
  --argjson now "$snapshot" \
  --argjson prev "$prev" \
  --arg interaction "$INTERACTION_ERROR_PREFIX" \
  --argjson reconnect_alert "$RECONNECT_ALERT" '
  def num(x): (if x == null then 0 else x end);
  def up($n; $name): ($n.upstreams // [] | map(select((.source // "") | startswith($name))) | first);

  ($now.upstreams // []) as $ups
  | ($prev.upstreams // null) as $prevups
  | ($prevups != null) as $hasprev
  | def prevof($name): (($prevups // []) | map(select((.source // "") | startswith($name))) | first);

  [
    if ($now.status // "") != "healthy" then
      {id:"status-not-healthy", severity:"warning",
       message:("health.status=" + ($now.status // "unknown") + " (Tier 2 absence is expected for Tier 1-only deployments)")}
    else empty end,

    ( $now.plugins.failed // 0 ) as $failed
    | if $failed > 0 then
        {id:"plugin-failed", severity:"critical",
         message:("failed plugins: " + ([$now.plugins.failures[]? | .id + ":" + .code] | join(", ")))}
      else empty end,

    ( $ups[] | select(.connected != true) |
        {id:"upstream-disconnected", severity:"critical",
         message:("upstream " + .source + " connected=false; lastError=" + (.lastError // "null"))} ),

    ( $ups[] |
      . as $u
      | (prevof($u.source)) as $p
      | (num($u.reconnectCount) - num($p.reconnectCount)) as $delta
      | if $hasprev and $delta > 0 and $reconnect_alert == 1 then
          {id:"reconnect", severity:"warning",
           message:("upstream " + $u.source + " reconnected " + ($delta|tostring) + " time(s) since the last check"
                    + "; lastDisconnectAt=" + ($u.lastDisconnectAt // "null")
                    + "; recorded reason=" + ($u.lastError // "null"))}
        else empty end ),

    # OBS-019: a disconnect happened but the reason was cleared by a successful reconnect.
    ( $ups[] |
      . as $u
      | (prevof($u.source)) as $p
      | (num($u.reconnectCount) - num($p.reconnectCount)) as $delta
      | if $hasprev and ($u.lastDisconnectAt != null) and ($u.lastError == null) and ($delta > 0) then
          {id:"disconnect-without-reason", severity:"warning",
           message:("OBS-019: " + $u.source + " dropped and reconnected but no reason is exposed"
                    + " (lastDisconnectAt=" + $u.lastDisconnectAt + ", lastError=null, reconnectCount="
                    + ((num($u.reconnectCount))|tostring) + ")."
                    + " Correlate with journalctl/docker logs around that timestamp.")}
        else empty end ),

    ( $ups[] | select(.lastError != null) |
        {id:(if (.lastError | contains($interaction)) then "interaction-ack-timeout" else "upstream-error" end),
         severity:(if (.lastError | contains($interaction)) then "critical" else "warning" end),
         message:("upstream " + .source + " lastError=" + .lastError)} ),

    ( $ups[] | select(num(.sendFailures) > 0) |
        {id:"send-failures", severity:"critical",
         message:("upstream " + .source + " sendFailures=" + (.sendFailures|tostring)
                  + " lastSendError=" + (.lastSendError // "null"))} ),

    ( $now.providers // [] | map(select(.supportTier == 1 and .available != true)) | .[] |
        {id:"tier1-provider-unavailable", severity:"critical",
         message:("Tier 1 provider unavailable: " + .id)} ),

    ( if $hasprev and num($now.tasks.failed) > num($prev.tasks.failed) then
        {id:"task-failed", severity:"warning",
         message:("P5 tasks failed: " + ((num($now.tasks.failed))|tostring))}
      else empty end )
  ] as $alerts
  | {
      checkedAt: (now | todate),
      sampledAt: $now.sampledAt,
      status: $now.status,
      alertCount: ($alerts | length),
      alerts: $alerts,
      snapshot: {
        upstreams: ($now.upstreams // [] | map({source, connected, reconnectCount, lastDisconnectAt, lastError, sendFailures})),
        plugins: $now.plugins,
        tasks: $now.tasks
      }
    }
')"

# Persist the current snapshot as the comparison baseline for the next run.
printf '%s' "$snapshot" >"$STATE_FILE"
chmod 600 "$STATE_FILE" 2>/dev/null || true

count="$(printf '%s' "$report" | jq -r '.alertCount')"

if [[ "$JSON" == "1" ]]; then
  printf '%s\n' "$report" | jq .
else
  printf '%s\n' "$report" | jq -r '
    "checked at \(.checkedAt)  status=\(.status)  alerts=\(.alertCount)",
    (.alerts[]? | "  [\(.severity)] \(.id): \(.message)")
  '
fi

if [[ "$count" != "0" ]]; then
  exit 1
fi
exit 0
