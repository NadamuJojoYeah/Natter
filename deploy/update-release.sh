#!/usr/bin/env bash
# Replace the running Natter release under the install root and restart the service.
#
# Why this exists: `dotnet publish -o /opt/natter` fails with MSB3191 because /opt needs root,
# and `sudo dotnet publish` would write obj/bin/NuGet cache as root. So publishing always happens
# in the workspace, and this script does the privileged copy step.
#
# It never touches configuration:
#   * /etc/natter (profiles, plugin config, secrets) is outside the install root;
#   * scripts/ and docs/ that the installer put under the root are preserved (copy, not mirror);
#   * the WebConsole audit directory is recreated so the systemd ReadWritePaths drop-in stays valid.
#
# Usage:
#   sudo deploy/update-release.sh --release artifacts/publish/rel-2026-11-22
#   sudo deploy/update-release.sh --release artifacts/publish/rel-2026-11-22 --no-restart
#   sudo deploy/update-release.sh --release ... --dry-run
#   sudo deploy/update-release.sh --release ... --target /srv/natter --unit natter.service
#
# Exit codes: 0 ok, 2 usage, 3 release dir invalid, 4 backup failed, 5 deploy failed, 6 verify failed.
set -euo pipefail

RELEASE=""
TARGET="/opt/natter"
UNIT="natter.service"
BACKUP_ROOT="/var/backups/natter"
RESTART=1
DRY_RUN=0
HEALTH_PORT="${NATTER_HEALTH_PORT:-5099}"

usage() {
  sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) RELEASE="${2:-}"; shift 2 ;;
    --target) TARGET="${2:-}"; shift 2 ;;
    --unit) UNIT="${2:-}"; shift 2 ;;
    --backup-root) BACKUP_ROOT="${2:-}"; shift 2 ;;
    --no-restart) RESTART=0; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
done

run() {
  if [[ $DRY_RUN -eq 1 ]]; then
    printf '[dry-run] %s\n' "$*"
  else
    "$@"
  fi
}

die() { echo "error: $*" >&2; exit "${2:-1}"; }

[[ -n "$RELEASE" ]] || die "--release is required" 2
[[ -d "$RELEASE" ]] || die "release directory not found: $RELEASE" 3
[[ -x "$RELEASE/natter" ]] || die "release has no executable 'natter': $RELEASE" 3
[[ -d "$RELEASE/plugins" ]] || die "release has no plugins directory: $RELEASE" 3

# Refuse a mixed build: every staged plugin must link host assemblies from this same release.
if [[ -f "$RELEASE/plugins/staging-manifest.json" ]]; then
  python3 - "$RELEASE/plugins/staging-manifest.json" <<'PY' || die "staging manifest reports host/plugin mismatch" 3
import json, sys
m = json.load(open(sys.argv[1]))
hp = m.get("hostParity") or {}
if not hp.get("checked"):
    print("warning: staging manifest has no hostParity check", file=sys.stderr)
elif hp.get("mismatches"):
    print(f"error: hostParity.mismatches={hp['mismatches']}", file=sys.stderr)
    sys.exit(1)
print(f"hostParity: compared={hp.get('comparedFiles')} mismatches={hp.get('mismatches')}")
PY
else
  echo "warning: no staging-manifest.json; cannot prove host/plugin parity" >&2
fi

if [[ $DRY_RUN -eq 0 ]] && [[ "$(id -u)" -ne 0 ]]; then
  die "must run as root (use sudo)" 2
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
BACKUP_DIR="$BACKUP_ROOT/$STAMP"

if [[ -d "$TARGET" ]]; then
  echo "backup: $TARGET -> $BACKUP_DIR"
  run install -d -m 0700 "$BACKUP_DIR" || die "cannot create backup dir" 4
  run tar -C "$(dirname "$TARGET")" -czf "$BACKUP_DIR/$(basename "$TARGET").tar.gz" "$(basename "$TARGET")" \
    || die "backup failed" 4
  echo "backup written: $BACKUP_DIR/$(basename "$TARGET").tar.gz"
else
  echo "note: $TARGET does not exist yet; skipping backup"
fi

# Preserve things the installer owns that are not part of the publish output.
KEEP_DIRS=()
for d in scripts docs; do
  if [[ -d "$TARGET/$d" ]]; then
    KEEP_DIRS+=("$d")
  fi
done
TMP_KEEP=""
if [[ ${#KEEP_DIRS[@]} -gt 0 ]]; then
  TMP_KEEP="$(mktemp -d)"
  for d in "${KEEP_DIRS[@]}"; do
    run cp -a "$TARGET/$d" "$TMP_KEEP/"
  done
fi

# A running host keeps /opt/natter/natter mapped; copying over it fails with ETXTBSY.
# Stop the unit first, and restore + restart if anything later fails.
STOPPED=0
cleanup_on_failure() {
  local rc=$?
  if [[ $rc -ne 0 && $STOPPED -eq 1 && $DRY_RUN -eq 0 ]]; then
    echo "deploy failed (rc=$rc); rolling back and restarting $UNIT" >&2
    local archive="$BACKUP_DIR/$(basename "$TARGET").tar.gz"
    if [[ -f "$archive" ]]; then
      tar -C "$(dirname "$TARGET")" -xzf "$archive" >/dev/null 2>&1 \
        && echo "rollback: restored from $archive" >&2 \
        || echo "rollback: FAILED to restore $archive" >&2
    fi
    systemctl start "$UNIT" >/dev/null 2>&1 \
      && echo "rollback: $UNIT started" >&2 \
      || echo "rollback: FAILED to start $UNIT" >&2
  fi
  exit $rc
}
trap cleanup_on_failure EXIT

if [[ $RESTART -eq 1 && $DRY_RUN -eq 0 ]]; then
  echo "stop: $UNIT (binary is mapped by the running host)"
  systemctl stop "$UNIT" || die "cannot stop $UNIT" 5
  STOPPED=1
  # The unit may take a moment to release the executable mapping.
  for _ in $(seq 1 20); do
    if ! fuser "$TARGET/natter" >/dev/null 2>&1; then
      break
    fi
    sleep 0.2
  done
fi

echo "deploy: $RELEASE -> $TARGET"
run install -d -m 0755 "$TARGET" || die "cannot create target" 5
# Copy the release on top; do not delete unknown files (config, audit, local state may live here).
run cp -a "$RELEASE/." "$TARGET/" || die "copy failed" 5

if [[ -n "$TMP_KEEP" ]]; then
  for d in "${KEEP_DIRS[@]}"; do
    run cp -a "$TMP_KEEP/$d/." "$TARGET/$d/"
  done
  rm -rf "$TMP_KEEP"
fi

# Plugins are read by the service user; a stray 0600 plugin.json breaks activation.
# Normalise ownership too: cp -a keeps the publisher's uid, which is not what the unit expects.
run chown -R root:root "$TARGET" || die "chown failed" 5
run chmod -R a+rX "$TARGET" || die "chmod failed" 5

# Plugin-owned data directories must stay writable by the service user.
#
# Rationale: a plugin owns its own data (audit logs, caches, plugin-local databases) and keeping it
# next to the plugin means "uninstall = delete the directory". The blanket `chown -R root:root` above
# is correct for CODE (a compromised service must not be able to rewrite its own or its plugins'
# binaries) but it also took away the one directory plugins legitimately write to. That contradiction
# is what produced the 2026-11-23 crash loop: the WebConsole audit directory ended up root-owned on a
# ProtectSystem=strict mount, so the scanner could not even enumerate it.
#
# So: code stays root-owned and read-only; only the `audit` subdirectory is handed to the service
# user, with 0750 so it is writable by natter and readable by admin groups.
SERVICE_USER="${NATTER_SERVICE_USER:-}"
if [[ -z "$SERVICE_USER" ]]; then
  # Prefer the unit's User= so the handover cannot drift from what systemd actually runs.
  SERVICE_USER="$(sed -n 's/^User=//p' "$TARGET/natter.service" 2>/dev/null | head -1 || true)"
fi
[[ -n "$SERVICE_USER" ]] || SERVICE_USER="natter"

# Plugins declare their writable data directories in plugin.json (`dataDirs`). Declaring them in the
# manifest keeps the deployment honest: adding a writing plugin no longer means editing this script,
# and the unit's ReadWritePaths can be checked against the same list.
PLUGIN_DATA_DIRS=()
shopt -s nullglob
for plugin_dir in "$TARGET"/plugins/*/; do
  plugin_dir="${plugin_dir%/}"
  manifest="$plugin_dir/plugin.json"
  [[ -f "$manifest" ]] || continue

  # `grep -qa` on the raw manifest avoids a JSON parser here (no jq dependency at deploy time).
  grep -qa '"dataDirs"' "$manifest" 2>/dev/null || continue

  while IFS= read -r rel; do
    [[ -n "$rel" ]] || continue
    case "$rel" in
      /*|*..*) echo "  WARNING: $manifest declares an unsafe dataDir '$rel' (must be a relative subpath); skipped" >&2; continue ;;
    esac
    PLUGIN_DATA_DIRS+=("$plugin_dir/$rel")
  done < <(sed -n '/"dataDirs"/,/\]/p' "$manifest" | sed -n 's/.*"path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
done
shopt -u nullglob

for data_dir in ${PLUGIN_DATA_DIRS[@]+"${PLUGIN_DATA_DIRS[@]}"}; do
  run install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$data_dir" \
    || die "cannot prepare plugin data dir $data_dir" 5
  echo "  plugin data: $data_dir ($SERVICE_USER:0750)"
  # A service-owned directory must not be readable by everyone; lib/ stays 0755 via chmod -R a+rX.
  run chmod 0750 "$data_dir" || true
done

if [[ ${#PLUGIN_DATA_DIRS[@]} -eq 0 ]]; then
  echo "  plugin data: (no plugin declared dataDirs)"
fi

echo "verify: release marker files"
[[ $DRY_RUN -eq 1 ]] || {
  "$TARGET/natter" version >/dev/null 2>&1 || die "release binary does not run" 6

  # Detect a stale staging directory. Two rules learned the hard way:
  #   1. Never pipe into `grep -q` under `set -o pipefail`: grep exits early, the producer
  #      gets SIGPIPE, and the pipeline reports failure even when the marker is present.
  #      `grep -qa <pattern> <file>` searches the binary directly, no pipeline involved.
  #   2. Pick markers that actually survive compilation. C# splits interpolated strings
  #      ("$\"Discord gateway watchdog ...\"") into fragments, so a log sentence is not a
  #      reliable marker; metadata identifiers are.
  require_marker() {
    local file="$1" pattern="$2" label="$3"
    if [[ ! -f "$file" ]]; then
      echo "  $label: skipped (not staged)"
      return 0
    fi
    if grep -qa -- "$pattern" "$file"; then
      echo "  $label: marker '$pattern' present"
    else
      die "$label artifact is stale (no '$pattern' marker)" 6
    fi
  }

  require_marker "$TARGET/plugins/natter.plugin.webconsole/lib/Natter.Plugin.WebConsole.dll" \
    "DiffOmitted" "webconsole"
  require_marker "$TARGET/plugins/discord/lib/Natter.Chat.Discord.dll" \
    "ReconnectWatchdog" "discord"
}

if [[ $RESTART -eq 1 ]]; then
  echo "start: $UNIT"
  run systemctl start "$UNIT" || die "start failed" 5
  STOPPED=0
  if [[ $DRY_RUN -eq 0 ]]; then
    sleep 12
    if command -v curl >/dev/null 2>&1; then
      BODY="$(curl -s --max-time 8 "http://127.0.0.1:$HEALTH_PORT/health" || true)"
      if [[ -z "$BODY" ]]; then
        die "health endpoint did not answer after restart" 6
      fi
      printf '%s' "$BODY" | python3 -c '
import json,sys
d=json.load(sys.stdin)
p=d.get("plugins") or {}
status=(d.get("status") or "unknown")
print("  status=", status, "uptime=", d.get("uptimeSeconds"), "plugins=", p.get("activated"), "/", p.get("failed"), "failed")
for u in d.get("upstreams") or []:
    print("  upstream", u.get("source"), "connected=", u.get("connected"))

# Four states (see RuntimeHealth): healthy / plugins-failed / degraded / unknown.
# - degraded  : core capability impaired (upstream down, Tier 1 provider unavailable) -> fail, roll back.
# - unknown   : no upstream configured -> cannot judge liveness -> fail loudly.
# - plugins-failed: host and upstreams are fine, but at least one plugin failed to load. This is the
#   EXPECTED state right after installing plugins that still need config/secrets, so failing here
#   would make the gate useless (every fresh install would "fail"). Warn, do not fail.
if status not in ("healthy", "plugins-failed"):
    print("  ERROR: status=", status, "-> core capability is impaired", file=sys.stderr)
    sys.exit(1)
if status == "plugins-failed":
    ids=[f.get("id") for f in (p.get("failures") or [])]
    print("  WARNING: plugins failed to load (configuration/secrets?):", ", ".join(x for x in ids if x))
    print("           core service is up; fix the plugins above or remove them from the release.")
sys.exit(0)
' || die "health check failed after restart (status=degraded/unknown)" 6
    fi
  fi
else
  echo "restart skipped (--no-restart)"
fi

echo "done."
echo "  release : $RELEASE"
echo "  target  : $TARGET"
if [[ -n "$BACKUP_DIR" && -d "$BACKUP_DIR" ]]; then
  echo "  rollback: sudo tar -C $(dirname "$TARGET") -xzf $BACKUP_DIR/$(basename "$TARGET").tar.gz"
fi
