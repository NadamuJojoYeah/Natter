#!/usr/bin/env bash
# Runs the opt-in live integration tests against the real DeepSeek and Discord APIs.
#
#   scripts/live-test.sh                 # all layers
#   scripts/live-test.sh deepseek        # provider adapter only (fast)
#   scripts/live-test.sh discord         # gateway, REST, upstream
#   scripts/live-test.sh ingress         # real gateway inbound -> NLMP
#   scripts/live-test.sh e2e             # NLMP/file-plugin -> DeepSeek -> Discord
#   scripts/live-test.sh deploy          # separate CLI process, file plugins, secrets
#   scripts/live-test.sh manual          # requires a human message in Discord
#
# Credentials come from artifacts/live/.env (git-ignored) or the process environment.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${NATTER_LIVE_ENV:-$ROOT/artifacts/live/.env}"
LAYER="${1:-all}"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a && source "$ENV_FILE" && set +a
else
  echo "No credential file at $ENV_FILE." >&2
  echo "Copy tests/Natter.Live.Tests/.env.example there and fill in the real values." >&2
  exit 2
fi

for name in DEEPSEEK_API_KEY DISCORD_BOT_TOKEN DISCORD_TEST_CHANNEL_ID DISCORD_BOT_USER_ID; do
  if [[ -z "${!name:-}" ]]; then
    echo "Missing $name in $ENV_FILE." >&2
    exit 2
  fi
done

export NATTER_LIVE=1

case "$LAYER" in
  all)      FILTER="Category=Live" ;;
  deepseek) FILTER="FullyQualifiedName~DeepSeekLiveTests" ;;
  discord)  FILTER="FullyQualifiedName~DiscordProbeLiveTests|FullyQualifiedName~DiscordUpstreamLiveTests" ;;
  ingress)  FILTER="FullyQualifiedName~GatewayIngressLiveTests" ;;
  e2e)      FILTER="FullyQualifiedName~OrchestrationLiveTests|FullyQualifiedName~FilePluginE2ELiveTests|FullyQualifiedName~RealInboundMentionLiveTests" ;;
  deploy)   FILTER="FullyQualifiedName~DeploymentCliLiveTests" ;;
  manual)
    export NATTER_LIVE_MANUAL=1
    FILTER="FullyQualifiedName~ManualGateLiveTests" ;;
  *)
    echo "Unknown layer '$LAYER'." >&2
    exit 2 ;;
esac

echo "== building live tests (Debug) =="
dotnet build "$ROOT/tests/Natter.Live.Tests/Natter.Live.Tests.csproj" -v q --nologo

echo "== running layer: $LAYER ($FILTER) =="
dotnet test "$ROOT/tests/Natter.Live.Tests/Natter.Live.Tests.csproj" \
  --no-build \
  --filter "$FILTER" \
  --logger "console;verbosity=normal" \
  ${NATTER_LIVE_TEST_ARGS:-}
