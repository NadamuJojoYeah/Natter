#!/usr/bin/env bash
# Reproducible end-to-end check for the WebConsole plugin.
#
#   ./scripts/webconsole-e2e.sh
#   WEBCONSOLE_E2E_PORT=5287 WEBCONSOLE_E2E_HEALTH_PORT=5299 ./scripts/webconsole-e2e.sh
#
# The script is loopback-only, uses a throwaway token and a throwaway state dir
# under artifacts/. It builds the CLI/plugin in Debug if needed, starts one host
# with --health-port, then asserts the auth/config/command/external-mode matrix.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$ROOT/artifacts/webconsole-e2e"
CONSOLE_PORT="${WEBCONSOLE_E2E_PORT:-5187}"
HEALTH_PORT="${WEBCONSOLE_E2E_HEALTH_PORT:-5199}"
TOKEN="${WEBCONSOLE_E2E_TOKEN:-e2e-token-0123456789abcdef}"
CLI="$ROOT/src/Natter.Cli/bin/Debug/net10.0/natter.dll"
JAR="$WORK/cookies.txt"
SERVE_PID=""

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

cleanup() {
  if [[ -n "$SERVE_PID" ]] && kill -0 "$SERVE_PID" 2>/dev/null; then
    kill "$SERVE_PID" 2>/dev/null || true
    wait "$SERVE_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

status() {
  local expected="$1"; shift
  local actual
  actual="$(curl -s -o "$WORK/last.json" -w '%{http_code}' "$@")"
  [[ "$actual" == "$expected" ]] || fail "expected HTTP $expected, got $actual: $(head -c 300 "$WORK/last.json")"
}

json_check() {
  local file="$1" expression="$2"
  python3 - "$file" "$expression" <<'PY'
import json, sys
payload = json.load(open(sys.argv[1], encoding="utf-8"))
value = eval(sys.argv[2], {"payload": payload})
if not value:
    raise SystemExit(f"assertion failed: {sys.argv[2]}\n{json.dumps(payload)[:600]}")
PY
}

echo "== build =="
[[ -f "$CLI" ]] || dotnet build "$ROOT/src/Natter.Cli/Natter.Cli.csproj" -c Debug -v q --nologo
dotnet build "$ROOT/plugins/Natter.Plugin.WebConsole/Natter.Plugin.WebConsole.csproj" -c Debug -v q --nologo

echo "== stage =="
rm -rf "$WORK"
mkdir -p "$WORK/config" "$WORK/state"
CONFIGURATION=Debug "$ROOT/scripts/stage-webconsole.sh" "$WORK/plugins" >/dev/null
cat >"$WORK/plugins/natter.plugin.webconsole/config.json" <<JSON
{
  "bind": "127.0.0.1",
  "port": $CONSOLE_PORT,
  "hostHealthUrl": "http://127.0.0.1:$HEALTH_PORT/health",
  "token": "\${secret:WEBCONSOLE_TOKEN}",
  "readOnly": false,
  "allowConfigWrite": true,
  "allowCommandConsole": true,
  "commandAllowlist": ["health", "plugins list", "upstreams"],
  "sessionMinutes": 60,
  "maxLogLines": 500,
  "trustedProxies": ["127.0.0.1"]
}
JSON

echo "== start host =="
WEBCONSOLE_TOKEN="$TOKEN" \
NATTER_CONFIG_DIR="$WORK/config" \
NATTER_STATE_DIR="$WORK/state" \
  dotnet "$CLI" --plugin-dir "$WORK/plugins" serve --duration 60 --health-port "$HEALTH_PORT" \
  >"$WORK/serve.log" 2>&1 &
SERVE_PID=$!

for _ in $(seq 1 60); do
  if [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$CONSOLE_PORT/" 2>/dev/null || true)" == "200" ]]; then
    break
  fi
  sleep 0.5
done
[[ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$CONSOLE_PORT/")" == "200" ]] \
  || fail "console did not start: $(tail -n 20 "$WORK/serve.log")"

echo "== auth =="
status 401 "http://127.0.0.1:$CONSOLE_PORT/api/snapshot"
status 401 -X POST -H 'Content-Type: application/json' --data '{"token":"wrong"}' \
  "http://127.0.0.1:$CONSOLE_PORT/api/session"
status 200 -c "$JAR" -X POST -H 'Content-Type: application/json' \
  --data "{\"token\":\"$TOKEN\"}" "http://127.0.0.1:$CONSOLE_PORT/api/session"
CSRF="$(python3 -c 'import json;print(json.load(open("'"$WORK"'/last.json"))["csrf"])')"
[[ -n "$CSRF" ]] || fail "missing csrf"

echo "== config validation =="
status 200 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"content":"{\"port\":5090}"}' "http://127.0.0.1:$CONSOLE_PORT/api/config/profiles/validate"
json_check "$WORK/last.json" 'payload["validation"]["valid"] is True'
json_check "$WORK/last.json" 'payload["validation"]["eTag"].startswith("sha256:")'

status 200 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"content":"{\"token\":\"plain-value\"}"}' "http://127.0.0.1:$CONSOLE_PORT/api/config/profiles/validate"
json_check "$WORK/last.json" 'payload["validation"]["valid"] is False'
json_check "$WORK/last.json" 'payload["validation"]["containsSecretLiteral"] is True'

status 400 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"content":"{oops}"}' "http://127.0.0.1:$CONSOLE_PORT/api/config/profiles"
status 501 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"content":"{}"}' "http://127.0.0.1:$CONSOLE_PORT/api/config/profiles"

echo "== command policy =="
status 200 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"command":"plugins   list","args":["--detail"]}' "http://127.0.0.1:$CONSOLE_PORT/api/commands/validate"
json_check "$WORK/last.json" 'payload["decision"]["allowed"] is True'
json_check "$WORK/last.json" 'payload["decision"]["command"] == "plugins list"'

status 403 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"command":"rm","args":["-rf","/"]}' "http://127.0.0.1:$CONSOLE_PORT/api/commands"
json_check "$WORK/last.json" 'payload["error"]["code"] == "command.not_allowed"'

status 501 -b "$JAR" -H "X-WebConsole-CSRF: $CSRF" -H 'Content-Type: application/json' \
  --data '{"command":"health","args":["--json"]}' "http://127.0.0.1:$CONSOLE_PORT/api/commands"

echo "== snapshot / audit / HEAD =="
status 200 -b "$JAR" "http://127.0.0.1:$CONSOLE_PORT/api/snapshot"
json_check "$WORK/last.json" 'any(c["name"] == "audit" and c["status"] == "ok" for c in payload["checks"])'
json_check "$WORK/last.json" 'payload["self"]["hostDataSource"] == "health-endpoint"'
[[ "$(curl -s -I -o /dev/null -w '%{http_code}' "http://127.0.0.1:$CONSOLE_PORT/")" == "200" ]] \
  || fail "HEAD / failed"

echo "== external mode =="
WEBCONSOLE_TOKEN="$TOKEN" \
NATTER_CONFIG_DIR="$WORK/config" \
NATTER_STATE_DIR="$WORK/state" \
  dotnet "$CLI" --plugin-dir "$WORK/plugins" web status --json >"$WORK/status.json" 2>"$WORK/status.log" \
  || fail "web status failed: $(cat "$WORK/status.log")"
json_check "$WORK/status.json" 'payload["server"]["state"] == "external"'

echo
echo "PASS: WebConsole E2E matrix"
echo "  console=http://127.0.0.1:$CONSOLE_PORT health=http://127.0.0.1:$HEALTH_PORT/health"
echo "  audit=$(find "$WORK/plugins/natter.plugin.webconsole/audit" -type f -maxdepth 1 | head -1)"
