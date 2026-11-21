#!/usr/bin/env bash
# Rotates one secret in a Natter live.env file without leaking the value.
#
# Design constraints (why this is not a one-liner):
#   * the value never appears on the command line (no `ps`, no shell history) — read from stdin;
#   * the file is replaced atomically, so a crash cannot leave a half-written env file;
#   * the previous file is kept as a 0600 backup for rollback;
#   * only the target key is rewritten; comments and other keys are preserved byte-for-byte;
#   * the value is reported as a SHA-256 fingerprint prefix so an operator can confirm the change
#     without the secret ever being printed.
#
# Usage:
#   printf '%s' "$NEW_TOKEN" | ./scripts/rotate-secret.sh --file /etc/natter/live.env --key DISCORD_BOT_TOKEN
#   ./scripts/rotate-secret.sh --file ./live.env --key DEEPSEEK_API_KEY --from-file ./new-key.txt
#   ./scripts/rotate-secret.sh --file /etc/natter/live.env --check
#   ./scripts/rotate-secret.sh --file /etc/natter/live.env --key DISCORD_BOT_TOKEN --dry-run
#
# Exit codes: 0 = ok, 1 = verification failed, 2 = usage, 3 = missing file/tool, 4 = unsafe value.
set -euo pipefail

FILE=""
KEY=""
FROM_FILE=""
CHECK=0
DRY_RUN=0
BACKUP_DIR=""

usage() {
  sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit 2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -f|--file) FILE="${2:-}"; shift 2 ;;
    -k|--key) KEY="${2:-}"; shift 2 ;;
    --from-file) FROM_FILE="${2:-}"; shift 2 ;;
    --backup-dir) BACKUP_DIR="${2:-}"; shift 2 ;;
    --check) CHECK=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

[[ -n "$FILE" ]] || { echo "missing --file" >&2; usage; }
[[ -f "$FILE" ]] || { echo "no such env file: $FILE" >&2; exit 3; }

fingerprint() {
  # Stable, non-reversible marker of the value; 8 hex chars are enough to confirm a rotation.
  printf '%s' "$1" | sha256sum | cut -c1-8
}

if [[ "$CHECK" == "1" ]]; then
  echo "== $FILE =="
  stat -c 'mode=%a owner=%U:%G size=%s' "$FILE"
  mode="$(stat -c '%a' "$FILE")"
  [[ "$mode" == "600" || "$mode" == "400" ]] || echo "  ! mode $mode is looser than 600"
  echo "keys:"
  while IFS= read -r line; do
    case "$line" in
      ''|'#'*) continue ;;
      *=*)
        name="${line%%=*}"
        value="${line#*=}"
        if [[ -z "$value" ]]; then
          printf '  %-24s <empty>\n' "$name"
        else
          printf '  %-24s len=%s sha256=%s\n' "$name" "${#value}" "$(fingerprint "$value")"
        fi
        ;;
    esac
  done <"$FILE"
  echo "note: values are never printed; compare fingerprints to confirm a rotation."
  exit 0
fi

[[ -n "$KEY" ]] || { echo "missing --key" >&2; usage; }
[[ "$KEY" =~ ^[A-Z][A-Z0-9_]*$ ]] || { echo "unsafe key name: $KEY" >&2; exit 4; }

if [[ -n "$FROM_FILE" ]]; then
  [[ -r "$FROM_FILE" ]] || { echo "cannot read --from-file: $FROM_FILE" >&2; exit 3; }
  new_value="$(tr -d '\r\n' <"$FROM_FILE")"
else
  # Read exactly one line from stdin; refuse when stdin is a terminal to avoid pasting into history.
  if [[ -t 0 ]]; then
    echo "refusing to read the secret from a terminal (it would leak into shell history)." >&2
    echo "pipe it instead: printf '%s' \"\$NEW\" | $0 --file $FILE --key $KEY" >&2
    exit 2
  fi
  new_value="$(head -n 1 | tr -d '\r\n')"
fi

if [[ -z "$new_value" ]]; then
  echo "empty secret value refused" >&2
  exit 4
fi
case "$new_value" in
  *"'"*|*'"'*|*'$'*|*'\'*|*' '*) echo "value contains quotes/whitespace/dollar/backslash; env files cannot carry it safely" >&2; exit 4 ;;
esac

old_value="$(sed -n "s/^${KEY}=//p" "$FILE" | head -1)"
old_fp="-"
[[ -n "$old_value" ]] && old_fp="$(fingerprint "$old_value")"
new_fp="$(fingerprint "$new_value")"

echo "== rotating $KEY in $FILE =="
echo "  old fingerprint: $old_fp"
echo "  new fingerprint: $new_fp"

if [[ "$old_fp" == "$new_fp" ]]; then
  echo "  value is unchanged; nothing to do"
  exit 0
fi

if [[ "$DRY_RUN" == "1" ]]; then
  echo "  [dry-run] would rewrite the file, keep a 0600 backup and chmod 600"
  exit 0
fi

# Same-directory temp file: rename() stays on one filesystem and is therefore atomic.
tmp="$(mktemp "${FILE}.tmp.XXXXXX")"
backup_dir="${BACKUP_DIR:-$(dirname "$FILE")}"
mkdir -p "$backup_dir"
backup="$backup_dir/$(basename "$FILE").bak.$(date +%Y%m%d-%H%M%S)"
cp -p "$FILE" "$backup"
chmod 600 "$backup"

# Preserve comments/order; replace the key in place, append when it was absent.
awk -v key="$KEY" -v value="$new_value" '
  BEGIN { done = 0 }
  $0 ~ "^" key "=" { print key "=" value; done = 1; next }
  { print }
  END { if (!done) print key "=" value }
' "$FILE" >"$tmp"

# Only the target line may change.
if ! diff <(grep -vE "^${KEY}=" "$FILE") <(grep -vE "^${KEY}=" "$tmp") >/dev/null 2>&1; then
  echo "  ! rewrite would touch other lines; aborting" >&2
  rm -f "$tmp"
  exit 1
fi

chmod 600 "$tmp"
mv -f "$tmp" "$FILE"
chmod 600 "$FILE"

written="$(sed -n "s/^${KEY}=//p" "$FILE" | head -1)"
if [[ "$(fingerprint "$written")" != "$new_fp" ]]; then
  echo "  ! verification failed after write; restoring $backup" >&2
  cp -p "$backup" "$FILE"
  chmod 600 "$FILE"
  exit 1
fi

cat <<EOF
  written: $FILE ($(stat -c '%a' "$FILE"))
  backup : $backup (0600)

next (per docs/operations/secret-rotation.md):
  1. restart the host so the new value is loaded:
       systemd: sudo systemctl restart natter && journalctl -u natter -n 50 --no-pager
       docker : docker compose -f deploy/docker/compose.yaml up -d --force-recreate
  2. verify:
       scripts/ops-health-alert.sh            # status healthy, no upstream-error alerts
       curl -sS http://127.0.0.1:5099/health | jq '.upstreams[] | {source, connected, lastError}'
  3. smoke one message per enabled upstream (Discord + DingTalk if deployed)
  4. revoke the old credential in the platform console afterwards
EOF
