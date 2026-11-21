#!/usr/bin/env bash
# Installs the Natter Tier 1 host as a systemd service on a real machine (requires root).
#
# This script prepares layout, ownership, permissions and the unit file. It never writes secret
# values: live.env is created as a 0600 template of empty keys that the operator fills in.
#
# Usage (as root):
#   sudo deploy/systemd/install.sh --release /opt/natter --config-src config
#   sudo deploy/systemd/install.sh --release /opt/natter --config-src config --start
#   deploy/systemd/install.sh --dry-run            # show what would happen, no root needed
#
# Exit codes: 0 = installed, 2 = usage, 3 = not running as root, 4 = missing release/config.
set -euo pipefail

RELEASE_DIR="/opt/natter"
CONFIG_DIR="/etc/natter/config"
STATE_DIR="/var/lib/natter"
LOG_DIR="/var/log/natter"
CONFIG_SRC=""
SERVICE_USER="natter"
START=0
DRY_RUN=0
UNIT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNIT_SRC="$UNIT_DIR/natter.service"
SOAK_UNIT_SRC="$UNIT_DIR/natter-soak.service"
SOAK_TIMER_SRC="$UNIT_DIR/natter-soak.timer"
SOAK_SCRIPT_SRC="$UNIT_DIR/../../scripts/soak-sample.sh"

usage() {
  sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) RELEASE_DIR="${2:-}"; shift 2 ;;
    --config-dir) CONFIG_DIR="${2:-}"; shift 2 ;;
    --config-src) CONFIG_SRC="${2:-}"; shift 2 ;;
    --state-dir) STATE_DIR="${2:-}"; shift 2 ;;
    --start) START=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
done

run() {
  if [[ "$DRY_RUN" == "1" ]]; then
    echo "  [dry-run] $*"
  else
    "$@"
  fi
}

[[ -f "$UNIT_SRC" ]] || { echo "unit file not found: $UNIT_SRC" >&2; exit 4; }
[[ -x "$RELEASE_DIR/natter" ]] || { echo "release not found or not executable: $RELEASE_DIR/natter" >&2; echo "publish first: dotnet publish src/Natter.Cli/Natter.Cli.csproj -c Release -o $RELEASE_DIR" >&2; exit 4; }

if [[ "$DRY_RUN" != "1" ]]; then
  if [[ "$(id -u)" != "0" ]]; then
    cat >&2 <<'EOF'
BLOCKED: this step needs root (it creates a system user, /etc and /var paths, and a systemd unit).
Run it on the target machine, or re-run with --dry-run to see the plan.
EOF
    exit 3
  fi
  if [[ "$(ps -p 1 -o comm= 2>/dev/null || true)" != "systemd" ]]; then
    echo "BLOCKED: PID 1 is not systemd on this host; the unit cannot be installed here." >&2
    exit 3
  fi
fi

echo "== Natter systemd install =="
echo "release    : $RELEASE_DIR"
echo "config     : $CONFIG_DIR"
echo "state      : $STATE_DIR"
echo "logs       : $LOG_DIR"
echo "unit source: $UNIT_SRC"
echo

echo "1/7 system user"
if id "$SERVICE_USER" >/dev/null 2>&1; then
  echo "  user exists: $SERVICE_USER"
else
  run useradd --system --home-dir "$RELEASE_DIR" --shell /usr/sbin/nologin --user-group "$SERVICE_USER"
fi

echo "2/7 release layout (read-only)"
run chown -R root:root "$RELEASE_DIR"
run chmod -R a+rX "$RELEASE_DIR"
run chmod -R go-w "$RELEASE_DIR"

echo "3/7 config layout"
run mkdir -p "$CONFIG_DIR"
if [[ -n "$CONFIG_SRC" ]]; then
  [[ -d "$CONFIG_SRC" ]] || { echo "config source not found: $CONFIG_SRC" >&2; exit 4; }
  # Copy manifests only; existing config.json files are left untouched.
  for f in "$CONFIG_SRC"/*.json; do
    [[ -e "$f" ]] || continue
    run cp -n "$f" "$CONFIG_DIR/"
  done
else
  echo "  no --config-src given: copy profiles.json and plugin config.json manually"
fi
run chown -R root:"$SERVICE_USER" /etc/natter
run chmod 750 /etc/natter
run chmod 640 "$CONFIG_DIR"/*.json 2>/dev/null || true

echo "4/7 secret and proxy env files (0600 template, no values written)"
if [[ ! -f /etc/natter/live.env ]]; then
  if [[ "$DRY_RUN" == "1" ]]; then
    echo "  [dry-run] create /etc/natter/live.env template (0600)"
  else
    umask 077
    cat >/etc/natter/live.env <<'ENV'
# Natter secrets — fill in values, keep this file 0600, never commit it.
# Rotation procedure: docs/operations/secret-rotation.md
DISCORD_BOT_TOKEN=
DEEPSEEK_API_KEY=
# Optional Tier 2:
# DINGTALK_CLIENT_ID=
# DINGTALK_CLIENT_SECRET=
# DINGTALK_BOT_ID=
# SPARK_API_KEY=
ENV
    echo "  created /etc/natter/live.env (fill in the values)"
  fi
fi
run chmod 600 /etc/natter/live.env
run chown root:root /etc/natter/live.env

if [[ ! -f /etc/natter/proxy.env ]]; then
  if [[ "$DRY_RUN" == "1" ]]; then
    echo "  [dry-run] create /etc/natter/proxy.env template (0640)"
  else
    cat >/etc/natter/proxy.env <<'ENV'
# Optional egress proxy for Discord. Leave empty for direct egress (recommended: the interaction
# ack budget is 3s, see docs/operations/discord-3s-interaction-egress.md).
# HTTP_PROXY=http://127.0.0.1:7890
# HTTPS_PROXY=http://127.0.0.1:7890
# ALL_PROXY=http://127.0.0.1:7890
NO_PROXY=127.0.0.1,localhost,::1
ENV
    echo "  created /etc/natter/proxy.env"
  fi
fi
run chmod 640 /etc/natter/proxy.env
run chown root:"$SERVICE_USER" /etc/natter/proxy.env

echo "5/7 state and log directories"
run mkdir -p "$STATE_DIR" "$LOG_DIR"
run chown -R "$SERVICE_USER:$SERVICE_USER" "$STATE_DIR" "$LOG_DIR"
run chmod 700 "$STATE_DIR" "$LOG_DIR"

echo "6/7 unit files and soak sampler"
run install -m 0644 "$UNIT_SRC" /etc/systemd/system/natter.service
if [[ -f "$SOAK_UNIT_SRC" && -f "$SOAK_TIMER_SRC" ]]; then
  # The soak timer runs the sampler from the release directory, so ship the script with it.
  run install -d -m 0755 "$RELEASE_DIR/scripts"
  if [[ -f "$SOAK_SCRIPT_SRC" ]]; then
    run install -m 0755 "$SOAK_SCRIPT_SRC" "$RELEASE_DIR/scripts/soak-sample.sh"
  else
    echo "  ! soak-sample.sh not found next to the unit; copy scripts/soak-sample.sh to $RELEASE_DIR/scripts manually" >&2
  fi
  run install -m 0644 "$SOAK_UNIT_SRC" /etc/systemd/system/natter-soak.service
  run install -m 0644 "$SOAK_TIMER_SRC" /etc/systemd/system/natter-soak.timer
else
  echo "  soak timer units not found; skipping passive sampling setup"
fi
if [[ "$DRY_RUN" != "1" ]]; then
  systemctl daemon-reload
  systemctl enable natter
  if [[ -f /etc/systemd/system/natter-soak.timer ]]; then
    systemctl enable --now natter-soak.timer
  fi
fi

echo "7/7 start"
if [[ "$START" == "1" ]]; then
  if [[ "$DRY_RUN" == "1" ]]; then
    echo "  [dry-run] systemctl start natter"
  else
    systemctl start natter
    sleep 3
    systemctl --no-pager --full status natter || true
  fi
else
  echo "  skipped (pass --start to start now)"
fi

cat <<EOF

next:
  1. fill /etc/natter/live.env, keep it 0600
  2. start/restart: sudo systemctl restart natter
  3. verify and record evidence:
       sudo deploy/systemd/verify.sh --evidence artifacts/ops/systemd-\$(date +%F)
  4. passive 24h soak sampling is already scheduled by natter-soak.timer
       systemctl list-timers natter-soak.timer
       sudo -u $SERVICE_USER tail -f $LOG_DIR/soak-\$(date +%F).jsonl
       needs jq on the host (the sampler uses it to merge sample metadata)
  5. after 24h, audit the run:
       python3 scripts/soak-report.py $LOG_DIR/soak-*.jsonl --interval 300
EOF
