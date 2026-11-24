#!/usr/bin/env bash
# Health/alert check for a running Natter host — the operational half of OBS-019.
#
# Disconnect attribution changed in P4.5 follow-up:
#   * the host now exposes `lastDisconnectError` / `lastDisconnectCode`, written on every disconnect
#     and **kept across Ready** (OBS-019 fix);
#   * `lastError` keeps its "current/latest error" meaning and is cleared on Ready, so it must NOT be
#     used to explain a disconnect any more.
#
# This script therefore reads the reason from `lastDisconnectError`/`lastDisconnectCode` first and
# only falls back to the legacy interpretation when those fields are absent (older host or older
# captured samples). One reconnect produces exactly one alert, so a drop is reported once instead of
# repeating on every poll.
#
# It changes nothing on the host and never starts a second gateway session: it reads the loopback
# /health endpoint, or a snapshot file when no host is running.
#
# Usage:
#   ./scripts/ops-health-alert.sh                                  # loopback :5099, default state
#   ./scripts/ops-health-alert.sh -p 5099 --state /var/lib/natter/log/alert-state.json
#   ./scripts/ops-health-alert.sh --file artifacts/.../health.json
#   ./scripts/ops-health-alert.sh --json                           # machine-readable report
#   ./scripts/ops-health-alert.sh --self-test                      # synthetic scenarios, no host needed
#
# Exit codes: 0 = no alert, 1 = alert raised, 2 = usage error, 3 = no snapshot available.
set -euo pipefail

PORT="${NATTER_HEALTH_PORT:-5099}"
STATE_FILE="${ALERT_STATE_FILE:-${NATTER_STATE_DIR:-/var/lib/natter}/log/alert-state.json}"
SNAPSHOT_FILE=""
JSON=0
# A reconnect is the only event that can be attributed; alert once per increment.
RECONNECT_ALERT=${RECONNECT_ALERT:-1}
# Discord interaction ack budget (3s) failures surface in lastError with this prefix.
INTERACTION_ERROR_PREFIX="interaction_defer_failed"
SELF_TEST=0

usage() {
  sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -p|--port) PORT="${2:-}"; shift 2 ;;
    --state) STATE_FILE="${2:-}"; shift 2 ;;
    --file) SNAPSHOT_FILE="${2:-}"; shift 2 ;;
    --json) JSON=1; shift ;;
    --self-test) SELF_TEST=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
done

require() {
  command -v "$1" >/dev/null 2>&1 || { echo "missing dependency: $1" >&2; exit 2; }
}
require jq

# ---------------------------------------------------------------------------------------------
# Alert evaluation. Kept in one jq program so the same rules run for loopback, snapshots and the
# self-test. Inputs: $now (current health), $prev (previous health, {} when there is no baseline),
# $interaction (error prefix), $reconnect_alert (0/1).
# ---------------------------------------------------------------------------------------------
ALERT_JQ='
  def num(x): (if x == null then 0 else x end);
  # "has a disconnect reason" is a property of the payload version, not of lastError.
  def has_reason_field($u): $u | has("lastDisconnectError");
  def reason_text($u):
    "code=" + (($u.lastDisconnectCode // "null") | tostring)
    + " error=" + (($u.lastDisconnectError // "null") | tostring);

  ($now.upstreams // []) as $ups
  | ($prev.upstreams // null) as $prevups
  | ($prevups != null) as $hasprev
  | def prevof($name): (($prevups // []) | map(select((.source // "") | startswith($name))) | first);

  [
    # `plugins-failed` is deliberately excluded: the host and upstreams are fine there, and the
    # dedicated `plugin-failed` rule below already reports each unloaded plugin. Alerting on the
    # aggregate status too would double-report and train operators to ignore this rule.
    if (($now.status // "") != "healthy") and (($now.status // "") != "plugins-failed") then
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
         message:("upstream " + .source + " connected=false; lastError=" + (.lastError // "null")
                  + "; lastDisconnect=" + (.lastDisconnectAt // "null")
                  + (if has(.lastDisconnectError) then " " + reason_text(.) else "" end))} ),

    # One alert per reconnect increment: attributed when the reason is available.
    ( $ups[] |
      . as $u
      | (prevof($u.source)) as $p
      | (num($u.reconnectCount) - num($p.reconnectCount)) as $delta
      | if $hasprev and $delta > 0 and $reconnect_alert == 1 then
          if ($u.lastDisconnectError // "") != "" then
            {id:"disconnect", severity:"warning",
             message:("upstream " + $u.source + " reconnected " + ($delta | tostring) + " time(s); last disconnect "
                      + ($u.lastDisconnectAt // "unknown") + " " + reason_text($u))}
          elif has_reason_field($u) then
            {id:"disconnect-without-reason", severity:"warning",
             message:("upstream " + $u.source + " reconnected " + ($delta | tostring) + " time(s) but the host reports"
                      + " lastDisconnectError=null (code=" + (($u.lastDisconnectCode // "null") | tostring) + ")."
                      + " The field exists, so this is a host that emits no message for this disconnect;"
                      + " correlate with journalctl/docker logs around " + ($u.lastDisconnectAt // "that time") + ".")}
          else
            {id:"disconnect-without-reason-legacy", severity:"warning",
             message:("upstream " + $u.source + " reconnected " + ($delta | tostring) + " time(s); this sample has no"
                      + " lastDisconnectError field (older host or captured before the OBS-019 fix), so the reason is"
                      + " only available in the logs around " + ($u.lastDisconnectAt // "that time") + ".")}
          end
        elif $hasprev and $delta < 0 then
          # A counter that goes backwards means a new process (restart), not a burst of reconnects.
          {id:"reconnect-counter-reset", severity:"warning",
           message:("upstream " + $u.source + " reconnectCount went " + (num($p.reconnectCount) | tostring)
                    + " -> " + (num($u.reconnectCount) | tostring) + " (host restarted?); lastDisconnectAt="
                    + ($u.lastDisconnectAt // "null"))}
        else empty end ),

    # lastError is the *current* error only (cleared on Ready); it is used for live failures, never
    # to explain a past disconnect.
    ( $ups[] | select(.lastError != null) |
        {id:(if (.lastError | contains($interaction)) then "interaction-ack-timeout" else "upstream-error" end),
         severity:(if (.lastError | contains($interaction)) then "critical" else "warning" end),
         message:("upstream " + .source + " lastError=" + .lastError)} ),

    ( $ups[] | select(num(.sendFailures) > 0) |
        {id:"send-failures", severity:"critical",
         message:("upstream " + .source + " sendFailures=" + (.sendFailures | tostring)
                  + " lastSendError=" + (.lastSendError // "null"))} ),

    ( $now.providers // [] | map(select(.supportTier == 1 and .available != true)) | .[] |
        {id:"tier1-provider-unavailable", severity:"critical",
         message:("Tier 1 provider unavailable: " + .id)} ),

    ( if $hasprev and num($now.tasks.failed) > num($prev.tasks.failed) then
        {id:"task-failed", severity:"warning",
         message:("P5 tasks failed: " + ((num($now.tasks.failed)) | tostring))}
      else empty end )
  ] as $alerts
  | {
      checkedAt: (now | todate),
      sampledAt: $now.sampledAt,
      status: $now.status,
      alertCount: ($alerts | length),
      alerts: $alerts,
      snapshot: {
        upstreams: ($now.upstreams // [] | map({
          source, connected, reconnectCount, lastDisconnectAt, lastError,
          lastDisconnectError, lastDisconnectCode, sendFailures
        })),
        plugins: $now.plugins,
        tasks: $now.tasks
      }
    }
'

evaluate() {
  local now="$1" prev="$2"
  jq -cn \
    --argjson now "$now" \
    --argjson prev "$prev" \
    --arg interaction "$INTERACTION_ERROR_PREFIX" \
    --argjson reconnect_alert "$RECONNECT_ALERT" \
    "$ALERT_JQ"
}

# ---------------------------------------------------------------------------------------------
# Self-test: synthetic payloads for both payload versions. No host, no network, no secrets.
# ---------------------------------------------------------------------------------------------
self_test() {
  local tmp
  tmp="$(mktemp -d)"

  local failures=0
  local state="$tmp/state.json"

  # Helpers: base payload with one discord upstream.
  snapshot() { # $1=reconnectCount $2=lastError $3=reason-mode(none|field|value|nofield) $4=code $5=err
    local reason="$3" code="${4:-null}" err="${5:-null}"
    local reason_json=""
    case "$reason" in
      value) reason_json=",\"lastDisconnectError\":\"$err\",\"lastDisconnectCode\":\"$code\"" ;;
      field) reason_json=",\"lastDisconnectError\":null,\"lastDisconnectCode\":null" ;;
      nofield) reason_json="" ;;          # legacy payload: the fields do not exist at all
      none) reason_json="" ;;
    esac
    cat <<JSON
{"status":"healthy","sampledAt":"2026-11-21T10:0$1:00Z",
 "upstreams":[{"source":"discord:discord:1","connected":true,"reconnectCount":$1,
   "lastDisconnectAt":"2026-11-21T09:59:50Z","lastConnectedAt":"2026-11-21T09:59:53Z",
   "lastError":$2,"sendFailures":0,"lastSendError":null${reason_json}}],
 "providers":[{"id":"natter.feature.ai.deepseek#deepseek","available":true,"supportTier":1}],
 "plugins":{"activated":4,"failed":0,"failures":[]},"tasks":{"failed":0}}
JSON
  }

  expect() { # $1=case-name $2=expected-exit $3=expected-alert-id $4=now $5=prev
    local name="$1" want_exit="$2" want_id="$3" now="$4" prev="$5"
    printf '%s' "$prev" >"$state"
    local out exit_code
    set +e
    out="$("$0" --file <(printf '%s' "$now") --state "$state" --json 2>/dev/null)"
    exit_code=$?
    set -e
    local ids
    ids="$(printf '%s' "$out" | jq -r '[.alerts[].id] | join(",")' 2>/dev/null || echo "<invalid-json>")"
    if [[ "$exit_code" != "$want_exit" ]]; then
      echo "  FAIL $name: exit=$exit_code want=$want_exit (alerts=$ids)" >&2
      failures=$((failures + 1))
      return
    fi
    if [[ "$want_id" == !* ]]; then
      local forbidden="${want_id#!}"
      if [[ ",$ids," == *",$forbidden,"* ]]; then
        echo "  FAIL $name: alerts=[$ids] must not contain $forbidden" >&2
        failures=$((failures + 1))
        return
      fi
    elif [[ -n "$want_id" && ",$ids," != *",$want_id,"* ]]; then
      echo "  FAIL $name: alerts=[$ids] missing $want_id" >&2
      failures=$((failures + 1))
      return
    fi
    if [[ -z "$want_id" && "$ids" != "" ]]; then
      echo "  FAIL $name: expected no alerts, got [$ids]" >&2
      failures=$((failures + 1))
      return
    fi
    echo "  ok   $name (exit=$exit_code alerts=[$ids])"
  }

  echo "== ops-health-alert self-test =="

  # 1. First run is the baseline: never alerts on history.
  printf '{}' >"$state"
  expect "baseline is silent" 0 "" "$(snapshot 3 null value ws_closed 'gateway closed (1006)')" '{}'

  # 2. New payload, reconnect + reason -> attributed alert, and the reason is used.
  expect "reconnect with reason" 1 "disconnect" \
    "$(snapshot 4 null value ws_closed 'gateway closed (1006)')" \
    "$(snapshot 3 null value ws_closed 'gateway closed (1006)')"

  # 3. New payload, reconnect but the host emitted no message.
  expect "reconnect without message (new field)" 1 "disconnect-without-reason" \
    "$(snapshot 4 null field null null)" \
    "$(snapshot 3 null field null null)"

  # 4. Legacy payload (fields absent) -> legacy wording, still no dependency on lastError.
  expect "legacy sample" 1 "disconnect-without-reason-legacy" \
    "$(snapshot 4 null nofield null null)" \
    "$(snapshot 3 null nofield null null)"

  # 5. Same snapshot twice -> no repeat alert for an old disconnect.
  expect "no repeat on unchanged snapshot" 0 "" \
    "$(snapshot 4 null value ws_closed 'gateway closed (1006)')" \
    "$(snapshot 4 null value ws_closed 'gateway closed (1006)')"

  # 6. A live error (lastError) must not be mistaken for a new disconnect: the reason field is
  #    populated and the counter did not move, so only the current-error alert may fire.
  expect "current error is not a disconnect" 1 "!disconnect" \
    "$(snapshot 4 '"transient: rate limited"' value ws_closed 'gateway closed (1006)')" \
    "$(snapshot 4 null value ws_closed 'gateway closed (1006)')"

  # 7. Counter reset after a host restart.
  expect "reconnect counter reset" 1 "reconnect-counter-reset" \
    "$(snapshot 0 null value ws_closed 'gateway closed (1006)')" \
    "$(snapshot 7 null value ws_closed 'gateway closed (1006)')"

  # 8. interaction ack timeout still uses lastError (that is its current-error meaning).
  expect "interaction ack timeout" 1 "interaction-ack-timeout" \
    "$(snapshot 4 '"interaction_defer_failed: Cannot defer an interaction after 3 seconds!"' value ws_closed 'gateway closed (1006)')" \
    "$(snapshot 4 null value ws_closed 'gateway closed (1006)')"

  rm -rf "$tmp"
  if [[ "$failures" -gt 0 ]]; then
    echo "self-test FAILED ($failures case(s))" >&2
    exit 1
  fi
  echo "self-test passed (8 cases)"
  exit 0
}

if [[ "$SELF_TEST" == "1" ]]; then
  self_test
fi

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

report="$(evaluate "$snapshot" "$prev")"

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
