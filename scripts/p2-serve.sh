#!/usr/bin/env bash
# Starts (or reports) the P2 four-plugin acceptance host used by docs/testing/2026-11-15-p2-acceptance.md.
#
#   scripts/p2-serve.sh --foreground [seconds]   # run in foreground (use with a background job / terminal)
#   scripts/p2-serve.sh --status                 # health + pid + log tail
#   scripts/p2-serve.sh --prepare                # only (re)stage plugins + config into the run dir
#
# Run dir defaults to artifacts/p2/<today>. Override with NATTER_P2_RUN.
# Credentials: ~/.config/natter/live.env (outside the repo, chmod 600).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_DIR="${NATTER_P2_RUN:-$ROOT/artifacts/p2/2026-11-15-1647}"
PORT="${NATTER_P2_PORT:-5096}"
ENV_FILE="${NATTER_LIVE_ENV:-$HOME/.config/natter/live.env}"
CLI_DLL="$ROOT/src/Natter.Cli/bin/Debug/net10.0/natter.dll"
MODE="${1:--foreground}"
SECONDS_TO_RUN="${2:-3600}"

stage() {
  mkdir -p "$RUN_DIR"/{config/all,plugins/all,state,evidence}
  for pair in "samples/Natter.Sample.Plugin.Discord:discord" "src/Natter.Feature.Ai.DeepSeek:deepseek" \
              "samples/Natter.Sample.Plugin.DingTalk:dingtalk" "src/Natter.Feature.Ai.Spark:spark"; do
    src="$ROOT/${pair%%:*}"; id="${pair##*:}"
    mkdir -p "$RUN_DIR/plugins/all/$id/lib"
    cp "$src/bin/Debug/net10.0/lib/"* "$RUN_DIR/plugins/all/$id/lib/"
    cp "$src/plugin.json" "$src/config.schema.json" "$RUN_DIR/plugins/all/$id/"
  done

  if [[ ! -f "$RUN_DIR/config/all/profiles.json" ]]; then
    cat > "$RUN_DIR/config/all/profiles.json" <<'JSON'
{"schemaVersion":1,"defaultProfile":"tier1-default","profiles":[
 {"id":"tier1-default","supportTier":1,"match":{"protocols":["discord"],"platforms":["discord"]},"ai":{"provider":"natter.feature.ai.deepseek#deepseek"},"history":{"scope":"conversation","maxMessages":20,"maxCharacters":24000}},
 {"id":"tier2-dingtalk-spark","supportTier":2,"match":{"protocols":["dingtalk"],"platforms":["dingtalk"]},"ai":{"provider":"natter.feature.ai.spark#spark"},"history":{"scope":"conversation","maxMessages":20,"maxCharacters":24000}}]}
JSON
  fi
  echo "staged -> $RUN_DIR"
}

if [[ "$MODE" == "--prepare" ]]; then
  stage
  exit 0
fi

if [[ "$MODE" == "--status" ]]; then
  curl -s -m 5 "http://127.0.0.1:$PORT/health" || true
  echo
  ls -t "$RUN_DIR/evidence"/serve-all.log 2>/dev/null | head -1 | xargs -r tail -n 8
  exit 0
fi

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Missing credential file: $ENV_FILE" >&2
  exit 2
fi
# shellcheck disable=SC1090
set -a && source "$ENV_FILE" && set +a

stage
echo "== serve on port $PORT for ${SECONDS_TO_RUN}s (run=$RUN_DIR) =="
exec dotnet "$CLI_DLL" --plugin-dir "$RUN_DIR/plugins/all" --config-dir "$RUN_DIR/config/all" \
  --state-dir "$RUN_DIR/state" --verbose serve --duration "$SECONDS_TO_RUN" --health-port "$PORT" \
  2>&1 | tee -a "$RUN_DIR/evidence/serve-all.log"
