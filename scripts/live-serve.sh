#!/usr/bin/env bash
# Deploys the two NPS plugins into a file-system plugin root and keeps a real natter host
# running so a human can talk to the bot in Discord.
#
#   scripts/live-serve.sh            # stage + start in background (default 7200s)
#   scripts/live-serve.sh 600        # run for 600 seconds
#   scripts/live-serve.sh --status   # is it running? show last log lines
#   scripts/live-serve.sh --stop     # stop the background host
#   scripts/live-serve.sh --foreground [seconds]
#                                    # run in the foreground (required inside a PID-namespace
#                                    # sandbox such as bwrap --unshare-pid, where setsid/nohup
#                                    # children die with the calling process)
#
# Credentials: artifacts/live/.env (git-ignored). Logs: artifacts/live/serve/*.log
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${NATTER_LIVE_ENV:-$HOME/.config/natter/live.env}"
if [[ ! -f "$ENV_FILE" ]]; then ENV_FILE="$ROOT/artifacts/live/.env"; fi
LIVE_DIR="${NATTER_LIVE_DIR:-$ROOT/artifacts/live}"
PLUGIN_ROOT="$LIVE_DIR/plugins"
SERVE_DIR="$LIVE_DIR/serve"
PID_FILE="$SERVE_DIR/natter.pid"
CLI_DLL="$ROOT/src/Natter.Cli/bin/Debug/net10.0/natter.dll"

ACTION="${1:-start}"

stop_host() {
  if [[ -f "$PID_FILE" ]]; then
    local pid
    pid="$(cat "$PID_FILE")"
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
      for _ in $(seq 1 20); do
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.2
      done
      kill -9 "$pid" 2>/dev/null || true
      echo "stopped pid=$pid"
    else
      echo "stale pid file ($pid not running)"
    fi
    rm -f "$PID_FILE"
  else
    echo "not running (no pid file)"
  fi
}

case "$ACTION" in
  --stop|stop)
    stop_host
    exit 0 ;;
  --status|status)
    if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
      echo "running pid=$(cat "$PID_FILE")"
      ls -t "$SERVE_DIR"/*.log 2>/dev/null | head -1 | xargs -r tail -n 20
    else
      echo "not running"
    fi
    exit 0 ;;
esac

FOREGROUND=0
if [[ "$ACTION" == "--foreground" ]]; then
  FOREGROUND=1
  SECONDS_TO_RUN="${2:-3600}"
else
  SECONDS_TO_RUN="${ACTION}"
fi
[[ "$SECONDS_TO_RUN" =~ ^[0-9]+$ ]] || SECONDS_TO_RUN=3600

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Missing credential file: $ENV_FILE" >&2
  exit 2
fi

# shellcheck disable=SC1090
set -a && source "$ENV_FILE" && set +a
for name in DEEPSEEK_API_KEY DISCORD_BOT_TOKEN DISCORD_TEST_GUILD_ID DISCORD_TEST_CHANNEL_ID DISCORD_BOT_USER_ID; do
  if [[ -z "${!name:-}" ]]; then
    echo "Missing $name in $ENV_FILE" >&2
    exit 2
  fi
done

echo "== building =="
dotnet build "$ROOT/src/Natter.Cli/Natter.Cli.csproj" -v q --nologo
dotnet build "$ROOT/samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj" -v q --nologo
dotnet build "$ROOT/src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj" -v q --nologo

stage_plugin() {
  local source="$1" destination="$2"
  rm -rf "$destination"
  mkdir -p "$destination/lib"
  cp "$source"/lib/* "$destination/lib/"
  cp "$source/plugin.json" "$source/config.schema.json" "$destination/"
}

echo "== staging plugins into $PLUGIN_ROOT =="
mkdir -p "$PLUGIN_ROOT" "$SERVE_DIR"
stage_plugin "$ROOT/samples/Natter.Sample.Plugin.Discord/bin/Debug/net10.0" "$PLUGIN_ROOT/discord"
stage_plugin "$ROOT/src/Natter.Feature.Ai.DeepSeek/bin/Debug/net10.0" "$PLUGIN_ROOT/deepseek"

DISCORD_PLUGIN_ROOT="$PLUGIN_ROOT/discord" DEEPSEEK_PLUGIN_ROOT="$PLUGIN_ROOT/deepseek" python3 - <<'PY'
import json, os
ids = {
    "selfId": os.environ["DISCORD_BOT_USER_ID"],
    "guild": os.environ["DISCORD_TEST_GUILD_ID"],
    "channel": os.environ["DISCORD_TEST_CHANNEL_ID"],
}
discord = {
    "token": "${secret:DISCORD_BOT_TOKEN}",
    "selfId": ids["selfId"],
    "intents": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
    "guilds": [ids["guild"]],
    "channels": [ids["channel"]],
    "requireMention": True,
    "ignoreBots": True,
    "ignoreSelf": True,
    "typing": True,
    "messageCommands": True,
    "slashCommands": False,
    "maxMessageLength": 1900,
    "readyTimeoutSeconds": 60,
}
deepseek = {
    "apiKey": "${secret:DEEPSEEK_API_KEY}",
    "model": "deepseek-flash",
    "thinking": "disabled",
    "reasoningEffort": "none",
    "systemPrompt": "You are Natter, a concise Discord assistant. Reply in the user's language in one or two short sentences.",
    "historyMaxMessages": 20,
    "historyMaxCharacters": 24000,
    "maxOutputTokens": 400,
    "timeoutSeconds": 60,
    "tools": [],
}
for path, payload in (
    (os.path.join(os.environ["DISCORD_PLUGIN_ROOT"], "config.json"), discord),
    (os.path.join(os.environ["DEEPSEEK_PLUGIN_ROOT"], "config.json"), deepseek),
):
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
PY

if [[ "$FOREGROUND" == "1" ]]; then
  FG_LOG="$SERVE_DIR/natter-fg-$(date +%Y%m%d-%H%M%S).log"
  echo "== foreground host for ${SECONDS_TO_RUN}s (log: $FG_LOG) =="
  echo "channel: $DISCORD_TEST_CHANNEL_ID  bot: $DISCORD_BOT_USER_ID  guild: $DISCORD_TEST_GUILD_ID"
  dotnet "$CLI_DLL" --plugin-dir "$PLUGIN_ROOT" --verbose \
    events watch --seconds "$SECONDS_TO_RUN" 2>&1 | tee -a "$FG_LOG"
  exit 0
fi

stop_host >/dev/null 2>&1 || true

LOG_FILE="$SERVE_DIR/natter-$(date +%Y%m%d-%H%M%S).log"
echo "== starting host for ${SECONDS_TO_RUN}s (log: $LOG_FILE) =="
setsid nohup dotnet "$CLI_DLL" \
  --plugin-dir "$PLUGIN_ROOT" \
  --verbose \
  events watch --seconds "$SECONDS_TO_RUN" >>"$LOG_FILE" 2>&1 &
PID=$!
echo "$PID" >"$PID_FILE"
sleep 6

if kill -0 "$PID" 2>/dev/null; then
  echo "running pid=$PID"
  echo "channel: $DISCORD_TEST_CHANNEL_ID  bot: $DISCORD_BOT_USER_ID  guild: $DISCORD_TEST_GUILD_ID"
  echo "---- first log lines ----"
  head -n 30 "$LOG_FILE" || true
else
  echo "host exited early; log:" >&2
  cat "$LOG_FILE" >&2
  exit 1
fi
