#!/usr/bin/env bash
# Collects systemd deployment evidence for a Natter host — and says BLOCKED instead of pretending.
#
# It is deliberately read-only: it never starts/stops the service and never reads secret values.
# If the machine has no systemd (containers, bwrap/PID-namespace sandboxes, WSL without systemd),
# it records the reason and exits 4, so a test record can never claim a systemd pass that did not
# happen.
#
# Usage:
#   deploy/systemd/verify.sh                                  # evidence under artifacts/ops/systemd-<date>/
#   sudo deploy/systemd/verify.sh --evidence /var/tmp/ev
#   deploy/systemd/verify.sh --unit natter --health-port 5099 --json
#
# Exit codes: 0 = evidence collected and checks passed, 1 = collected but checks failed,
#             3 = usage/environment error, 4 = BLOCKED (no usable systemd here).
set -uo pipefail

UNIT="natter"
HEALTH_PORT="${NATTER_HEALTH_PORT:-5099}"
EVIDENCE_DIR=""
JSON=0
NATTER_BIN="${NATTER_BIN:-/opt/natter/natter}"
CONFIG_DIR="${NATTER_CONFIG_DIR:-/etc/natter/config}"
STATE_DIR="${NATTER_STATE_DIR:-/var/lib/natter}"

usage() {
  sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 3
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --unit) UNIT="${2:-}"; shift 2 ;;
    --health-port) HEALTH_PORT="${2:-}"; shift 2 ;;
    --evidence) EVIDENCE_DIR="${2:-}"; shift 2 ;;
    --natter) NATTER_BIN="${2:-}"; shift 2 ;;
    --config-dir) CONFIG_DIR="${2:-}"; shift 2 ;;
    --state-dir) STATE_DIR="${2:-}"; shift 2 ;;
    --json) JSON=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

if [[ -z "$EVIDENCE_DIR" ]]; then
  EVIDENCE_DIR="artifacts/ops/systemd-$(date +%F)"
fi
mkdir -p "$EVIDENCE_DIR" || { echo "cannot create evidence dir: $EVIDENCE_DIR" >&2; exit 3; }

declare -a checks=() failures=()
record() {
  checks+=("$1")
  if [[ "$2" == "fail" ]]; then failures+=("$1"); fi
  return 0
}
write() { printf '%s\n' "$2" >"$EVIDENCE_DIR/$1"; }

pid1="$(ps -p 1 -o comm= 2>/dev/null | tr -d ' ' || true)"
has_systemctl=0
command -v systemctl >/dev/null 2>&1 && has_systemctl=1

# "systemctl can talk to a systemd manager" is not the same as "we may install/start a unit here".
systemd_ok=0
if [[ "$has_systemctl" == "1" ]] && systemctl list-units --no-pager >/dev/null 2>&1; then
  systemd_ok=1
fi
unit_installed=0
[[ -f "/etc/systemd/system/${UNIT}.service" ]] && unit_installed=1

write meta.txt "collectedAt: $(date -Is)
host: $(uname -a)
pid1: ${pid1:-unknown}
userId: $(id -u)
systemctl: $([[ "$has_systemctl" == 1 ]] && echo present || echo missing)
unit: $UNIT
healthPort: $HEALTH_PORT
natterBin: $NATTER_BIN
configDir: $CONFIG_DIR
stateDir: $STATE_DIR"

blocked_reason=""
if [[ "$systemd_ok" != "1" ]]; then
  blocked_reason="no usable systemd manager (pid1=${pid1:-unknown}, systemctl=$([[ "$has_systemctl" == 1 ]] && echo present || echo missing))"
elif [[ "$unit_installed" != "1" && "$(id -u)" != "0" ]]; then
  blocked_reason="unit ${UNIT}.service is not installed and installing it needs root (uid=$(id -u), pid1=${pid1:-unknown})"
elif [[ "$unit_installed" != "1" ]]; then
  blocked_reason="unit ${UNIT}.service is not installed; run deploy/systemd/install.sh first"
fi

if [[ -n "$blocked_reason" ]]; then
  write BLOCKED.txt "BLOCKED: ${blocked_reason}

Nothing was installed, started or measured. This file is the environment limitation record —
do not report a systemd pass from this host.
pid1=${pid1:-unknown}
systemctl=$([[ "$has_systemctl" == 1 ]] && echo present || echo missing)
unitInstalled=${unit_installed}
uid=$(id -u)
next: run deploy/systemd/install.sh as root on the target machine, then re-run this script there."
  if [[ "$JSON" == "1" ]]; then
    printf '{"result":"BLOCKED","reason":"%s","pid1":"%s","unitInstalled":%s,"evidenceDir":"%s"}\n' \
      "$(printf '%s' "$blocked_reason" | sed 's/"/\\"/g')" "${pid1:-unknown}" "$unit_installed" "$EVIDENCE_DIR"
  else
    echo "BLOCKED: $blocked_reason"
    echo "evidence: $EVIDENCE_DIR/BLOCKED.txt"
  fi
  exit 4
fi

echo "== collecting systemd evidence into $EVIDENCE_DIR =="

systemctl is-enabled "$UNIT" >"$EVIDENCE_DIR/is-enabled.txt" 2>&1
systemctl is-active "$UNIT" >"$EVIDENCE_DIR/is-active.txt" 2>&1
systemctl --no-pager --full status "$UNIT" >"$EVIDENCE_DIR/status.txt" 2>&1
systemctl show "$UNIT" -p ActiveState,SubState,MainPID,NRestarts,ExecMainStartTimestamp,Environment,EnvironmentFiles,User,Group,Restart,StateDirectory,LogsDirectory >"$EVIDENCE_DIR/show.txt" 2>&1
journalctl -u "$UNIT" -n 300 --no-pager >"$EVIDENCE_DIR/journal-tail.txt" 2>&1 || true

[[ "$(cat "$EVIDENCE_DIR/is-enabled.txt")" =~ ^enabled ]] && record "unit enabled" pass || record "unit enabled" fail
[[ "$(cat "$EVIDENCE_DIR/is-active.txt")" =~ ^active ]] && record "unit active" pass || record "unit active" fail

# Ownership / permission expectations from the runbook (§1.1, §3.2).
for spec in "$CONFIG_DIR:ro" "/etc/natter/live.env:600" "/etc/natter/proxy.env:640|missing-ok" "$STATE_DIR:700"; do
  path="${spec%%:*}"; want="${spec#*:}"
  if [[ -e "$path" ]]; then
    actual="$(stat -c '%a %U:%G' "$path" 2>/dev/null || echo unknown)"
    write "perm-$(echo "$path" | tr '/' '_').txt" "$path -> $actual (expected mode ${want%%|*})"
    if [[ "$path" == "/etc/natter/live.env" ]]; then
      [[ "$(stat -c '%a' "$path")" == "600" ]] && record "live.env 0600" pass || record "live.env 0600" fail
    fi
    if [[ "$path" == "$STATE_DIR" ]]; then
      [[ "$(stat -c '%a' "$path")" == "700" ]] && record "state dir 0700" pass || record "state dir 0700" fail
    fi
  else
    write "perm-$(echo "$path" | tr '/' '_').txt" "MISSING: $path"
    [[ "$want" == *"missing-ok"* ]] || record "path exists: $path" fail
  fi
done

# Loopback health snapshot (never publishes the port).
if curl -fsS --max-time 5 "http://127.0.0.1:${HEALTH_PORT}/health" >"$EVIDENCE_DIR/health-http.json" 2>"$EVIDENCE_DIR/health-http.err"; then
  record "loopback /health reachable" pass
  if command -v jq >/dev/null 2>&1; then
    jq -r '.status, (.upstreams[]? | "\(.source) connected=\(.connected) reconnectCount=\(.reconnectCount) lastError=\(.lastError)")' \
      "$EVIDENCE_DIR/health-http.json" >"$EVIDENCE_DIR/health-summary.txt" 2>/dev/null || true
    [[ "$(jq -r '.status // "unknown"' "$EVIDENCE_DIR/health-http.json")" == "healthy" ]] \
      && record "status healthy" pass || record "status healthy" fail
  fi
else
  record "loopback /health reachable" fail
  echo "unreachable" >"$EVIDENCE_DIR/health-http.err"
fi

# Offline CLI view: proves config/plugin wiring without opening a second gateway session.
if [[ -x "$NATTER_BIN" ]]; then
  "$NATTER_BIN" --config-dir "$CONFIG_DIR" --state-dir "$STATE_DIR" health --offline --json \
    >"$EVIDENCE_DIR/health-offline.json" 2>"$EVIDENCE_DIR/health-offline.err" || true
else
  echo "missing: $NATTER_BIN" >"$EVIDENCE_DIR/health-offline.err"
fi

# State database sanity (no writes).
if [[ -f "$STATE_DIR/natter.db" ]]; then
  if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$STATE_DIR/natter.db" 'pragma integrity_check;' >"$EVIDENCE_DIR/db-integrity.txt" 2>&1 || true
    grep -qx ok "$EVIDENCE_DIR/db-integrity.txt" && record "db integrity ok" pass || record "db integrity ok" fail
  else
    echo "sqlite3 not installed" >"$EVIDENCE_DIR/db-integrity.txt"
  fi
  stat -c '%s bytes %a %U:%G %n' "$STATE_DIR"/natter.db* >"$EVIDENCE_DIR/db-files.txt" 2>&1 || true
fi

{
  echo "checks:"
  for check in "${checks[@]}"; do echo "  - $check"; done
  echo
  echo "failures: ${#failures[@]}"
  for failure in "${failures[@]}"; do echo "  ! $failure"; done
} | tee "$EVIDENCE_DIR/summary.txt"

result="PASS"; [[ "${#failures[@]}" -gt 0 ]] && result="FAIL"

if [[ "$JSON" == "1" ]]; then
  printf '{"result":"%s","evidenceDir":"%s","checks":%s,"failures":%s}\n' \
    "$result" "$EVIDENCE_DIR" "${#checks[@]}" "${#failures[@]}"
fi

[[ "$result" == "PASS" ]] && exit 0 || exit 1
