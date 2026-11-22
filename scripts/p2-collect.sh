#!/usr/bin/env bash
# Collects P2 acceptance evidence after a human sends real messages.
#
#   scripts/p2-collect.sh artifacts/p2/<run> [health-port]
#
# Writes into <run>/evidence/collect-<HHmmss>/:
#   health.json, serve.log.tail, sqlite.txt, discord-tail.json, env.txt
# Never prints secrets.
set -euo pipefail

RUN_DIR="${1:?usage: p2-collect.sh <run-dir> [health-port]}"
PORT="${2:-5096}"
STAMP="$(date +%H%M%S)"
OUT="$RUN_DIR/evidence/collect-$STAMP"
mkdir -p "$OUT"

echo "run=$RUN_DIR port=$PORT out=$OUT"

curl -s -m 5 "http://127.0.0.1:$PORT/health" -o "$OUT/health.json" || true

tail -n 200 "$RUN_DIR/evidence/serve-all.log" > "$OUT/serve.log.tail" 2>/dev/null || true

if [[ -f "$RUN_DIR/state/natter.db" ]]; then
  {
    echo "# tables"; sqlite3 "$RUN_DIR/state/natter.db" ".tables"
    echo "# sessions"; sqlite3 "$RUN_DIR/state/natter.db" "select source_key, conversation_id, coalesce(actor_id,'-'), version from sessions;"
    echo "# messages"; sqlite3 "$RUN_DIR/state/natter.db" "select session_id, role, substr(text,1,60), source_event_id from messages order by message_id;"
    echo "# counts"; sqlite3 "$RUN_DIR/state/natter.db" "select (select count(*) from sessions), (select count(*) from messages);"
  } > "$OUT/sqlite.txt" 2>&1
fi

# Discord side (optional; skips when credentials are absent).
if [[ -f "$HOME/.config/natter/live.env" ]]; then
  # shellcheck disable=SC1090
  set -a && source "$HOME/.config/natter/live.env" && set +a
  if [[ -n "${DISCORD_BOT_TOKEN:-}" && -n "${DISCORD_TEST_CHANNEL_ID:-}" ]]; then
    # S2-E10: never pass the token as an argument — argv is world-readable via ps and
    # /proc/<pid>/cmdline. curl reads its config from stdin (`-K -`), so only the URL is visible.
    printf 'header = "Authorization: Bot %s"\n' "$DISCORD_BOT_TOKEN" \
      | curl -s -m 10 -K - -o "$OUT/discord-tail.json" \
          "https://discord.com/api/v10/channels/$DISCORD_TEST_CHANNEL_ID/messages?limit=10" || true
  fi
fi

echo "serve_log_lines=$(wc -l < "$OUT/serve.log.tail" 2>/dev/null || echo 0)"
if [[ -f "$OUT/sqlite.txt" ]]; then
  tail -n 1 "$OUT/sqlite.txt"
fi
echo "collected -> $OUT"
