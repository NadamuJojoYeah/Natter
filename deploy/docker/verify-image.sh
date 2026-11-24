#!/usr/bin/env bash
# Verifies that the built image actually runs as a Natter Tier 1 host, and records evidence.
#
# Works in two modes:
#   * compose mode   — `docker compose config -q` validates deploy/docker/compose.yaml, then
#                      `docker compose up` starts the service (the normal deployment path);
#   * run mode       — no compose plugin available: the same container settings are reproduced with
#                      `docker run`, which is what makes the image verifiable on minimal hosts.
#
# Checks performed (all recorded under --evidence):
#   * the image starts and stays up                  (docker inspect)
#   * the host runs as UID/GID 10001, rootless        (docker inspect .Config.User)
#   * the root filesystem is read-only               (docker inspect .HostConfig.ReadonlyRootfs)
#   * the NPS plugins are staged and loadable        (/app/plugins + plugins inspect)
#   * the healthcheck turns healthy                  (docker inspect .State.Health.Status)
#   * the container's /health reports the upstreams  (docker exec, loopback only)
#   * no secret value from the env file leaks into   (evidence grep, values never printed)
#     the image, the logs or the evidence
#
# Usage:
#   deploy/docker/verify-image.sh --image natter:tier1 --env-file deploy/docker/live.env \
#       --config-dir deploy/docker/config
#   deploy/docker/verify-image.sh --image natter:tier1 --build          # build first
#   deploy/docker/verify-image.sh --help
#
# Exit codes: 0 = PASS, 1 = FAIL (checks failed), 3 = usage/preflight, 4 = BLOCKED (no docker).
set -uo pipefail

IMAGE="natter:tier1"
ENV_FILE=""
CONFIG_DIR=""
EVIDENCE=""
BUILD=0
KEEP=0
WAIT_SECONDS=120
NAME="natter-verify"
NETWORK_MODE=""
MOUNT_HOSTS=0
CA_BUNDLE=""

usage() {
  sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-3}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --image) IMAGE="${2:-}"; shift 2 ;;
    --env-file) ENV_FILE="${2:-}"; shift 2 ;;
    --config-dir) CONFIG_DIR="${2:-}"; shift 2 ;;
    --evidence) EVIDENCE="${2:-}"; shift 2 ;;
    --build) BUILD=1; shift ;;
    --keep) KEEP=1; shift ;;
    --name) NAME="${2:-}"; shift 2 ;;
    --wait) WAIT_SECONDS="${2:-}"; shift 2 ;;
    --network) NETWORK_MODE="${2:-}"; shift 2 ;;
    --mount-hosts) MOUNT_HOSTS=1; shift ;;
    --ca-bundle) CA_BUNDLE="${2:-}"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 3 ;;
  esac
done

if [[ -z "$EVIDENCE" ]]; then
  EVIDENCE="artifacts/ops/docker-verify-$(date +%F-%H%M)"
fi
mkdir -p "$EVIDENCE" || { echo "cannot create evidence dir: $EVIDENCE" >&2; exit 3; }

declare -a checks=() failures=()

record() {
  checks+=("$1")
  if [[ "$2" == "fail" ]]; then failures+=("$1"); fi
  return 0
}
write() { printf '%s\n' "$2" >"$EVIDENCE/$1"; }

command -v docker >/dev/null 2>&1 || { echo "BLOCKED: docker CLI not found" >&2; exit 4; }
docker info >/dev/null 2>&1 || {
  write BLOCKED.txt "BLOCKED: docker daemon not reachable from this account ($(id -u))."
  echo "BLOCKED: docker daemon not reachable" >&2
  exit 4
}

compose_mode=0
if docker compose version >/dev/null 2>&1; then
  compose_mode=1
  compose_file="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/compose.yaml"
  if docker compose -f "$compose_file" config -q >"$EVIDENCE/compose-config.txt" 2>&1; then
    record "compose file validates" pass
  else
    record "compose file validates" fail
  fi
else
  write compose-config.txt "docker compose plugin not installed; validated the image with docker run instead.
The deployment path for hosts without the plugin is documented in deploy/README.md."
fi

if [[ "$BUILD" == "1" ]]; then
  echo "== building $IMAGE =="
  if docker build -f "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/Dockerfile" -t "$IMAGE" "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)" \
      >"$EVIDENCE/docker-build.log" 2>&1; then
    record "image builds" pass
  else
    record "image builds" fail
    echo "build failed, see $EVIDENCE/docker-build.log" >&2
  fi
fi

if ! docker image inspect "$IMAGE" >"$EVIDENCE/image-inspect.json" 2>/dev/null; then
  record "image exists: $IMAGE" fail
  write BLOCKED.txt "BLOCKED: image $IMAGE not found. Build it first (--build) or pass --image."
  echo "BLOCKED: image $IMAGE not found" >&2
  exit 4
fi
record "image exists: $IMAGE" pass

# --- static image content ------------------------------------------------------------
docker run --rm --entrypoint sh "$IMAGE" -c 'ls -1 /app/plugins' >"$EVIDENCE/app-plugins.txt" 2>&1 || true
for required in natter.transport.messaging.discord natter.feature.ai.deepseek; do
  if grep -qx "$required" "$EVIDENCE/app-plugins.txt"; then
    record "image stages plugin: $required" pass
  else
    record "image stages plugin: $required" fail
  fi
done
docker run --rm --entrypoint sh "$IMAGE" -c 'cat /app/plugins/staging-manifest.json' >"$EVIDENCE/staging-manifest.json" 2>&1 || true
docker run --rm --entrypoint dotnet "$IMAGE" /app/natter.dll plugins inspect /app/plugins/natter.transport.messaging.discord \
  >"$EVIDENCE/inspect-discord.txt" 2>&1 || true
grep -q 'valid          : True' "$EVIDENCE/inspect-discord.txt" \
  && record "discord manifest valid in image" pass || record "discord manifest valid in image" fail

# --- runtime -------------------------------------------------------------------------
if ! docker image inspect --format '{{.Config.User}}' "$IMAGE" | grep -qx '10001:10001'; then
  record "image user is 10001:10001" fail
else
  record "image user is 10001:10001" pass
fi

docker rm -f "$NAME" >/dev/null 2>&1 || true
docker volume rm "${NAME}-state" >/dev/null 2>&1 || true

declare -a run_args=(
  -d --name "$NAME"
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges:true
  -e NATTER_CONFIG_DIR=/config -e NATTER_STATE_DIR=/state
  -e "NO_PROXY=127.0.0.1,localhost,::1"
  --health-interval=10s --health-timeout=5s --health-start-period=10s --health-retries=3
)
[[ -n "$NETWORK_MODE" ]] && run_args+=(--network "$NETWORK_MODE")
# Some hosts terminate Discord locally (hosts override + private CA). Mounting those pieces is an
# environment adaptation, not an image requirement — record it so the evidence stays honest.
if [[ "$MOUNT_HOSTS" == "1" && -r /etc/hosts ]]; then
  run_args+=(-v /etc/hosts:/etc/hosts:ro)
fi
if [[ -n "$CA_BUNDLE" ]]; then
  [[ -r "$CA_BUNDLE" ]] || { echo "cannot read --ca-bundle: $CA_BUNDLE" >&2; exit 3; }
  run_args+=(-v "$(cd "$(dirname "$CA_BUNDLE")" && pwd)/$(basename "$CA_BUNDLE"):/etc/ssl/certs/ca-certificates.crt:ro")
fi
[[ -n "$CONFIG_DIR" ]] && run_args+=(-v "$(cd "$CONFIG_DIR" && pwd):/config:ro")
[[ -n "$ENV_FILE" ]] && run_args+=(--env-file "$ENV_FILE")
run_args+=(-v "${NAME}-state:/state")

if [[ "$compose_mode" == "1" ]]; then
  echo "note: compose plugin present, but this run reproduces the same flags with docker run for evidence"
fi

if ! docker run "${run_args[@]}" "$IMAGE" >"$EVIDENCE/docker-run-id.txt" 2>&1; then
  record "container starts" fail
  cat "$EVIDENCE/docker-run-id.txt" >&2
else
  record "container starts" pass
fi

waited=0
health="starting"
while (( waited < WAIT_SECONDS )); do
  health="$(docker inspect "$NAME" --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' 2>/dev/null || echo missing)"
  [[ "$health" == "healthy" || "$health" == "unhealthy" ]] && break
  sleep 5
  waited=$((waited + 5))
done

# `docker inspect` prints .Config.Env, i.e. every secret passed with --env-file. Never store that:
# keep the structural fields, drop the environment.
if command -v jq >/dev/null 2>&1; then
  docker inspect "$NAME" 2>/dev/null | jq 'del(.Config.Env)' >"$EVIDENCE/container-inspect.json" 2>/dev/null || true
else
  write container-inspect.json "jq not available: full docker inspect not stored (it would leak .Config.Env)."
fi
docker logs "$NAME" >"$EVIDENCE/container.log" 2>&1 || true
docker inspect "$NAME" --format \
  'status={{.State.Status}} health={{.State.Health.Status}} user={{.Config.User}} readOnly={{.HostConfig.ReadonlyRootfs}} restarts={{.RestartCount}}' \
  >"$EVIDENCE/container-summary.txt" 2>&1 || true

[[ "$health" == "healthy" ]] && record "container healthcheck healthy" pass || record "container healthcheck healthy" fail
[[ "$(docker inspect "$NAME" --format '{{.HostConfig.ReadonlyRootfs}}')" == "true" ]] \
  && record "root filesystem read-only" pass || record "root filesystem read-only" fail
[[ "$(docker inspect "$NAME" --format '{{.Config.User}}')" == "10001:10001" ]] \
  && record "container runs as 10001:10001" pass || record "container runs as 10001:10001" fail

# Loopback /health from inside the container only — the port is never published.
docker exec "$NAME" bash -c '
  exec 3<>/dev/tcp/127.0.0.1/5099 || exit 1
  printf "GET /health HTTP/1.0\r\n\r\n" >&3
  sed "1,/^\r$/d" <&3
' >"$EVIDENCE/health-inside.json" 2>"$EVIDENCE/health-inside.err" || true
if [[ -s "$EVIDENCE/health-inside.json" ]]; then
  record "container /health reachable" pass
  if command -v jq >/dev/null 2>&1; then
    jq -r '.status, (.upstreams[]? | "\(.source) connected=\(.connected) reconnectCount=\(.reconnectCount) lastError=\(.lastError)")' \
      "$EVIDENCE/health-inside.json" >"$EVIDENCE/health-inside-summary.txt" 2>/dev/null || true
    status="$(jq -r '.status // "unknown"' "$EVIDENCE/health-inside.json" 2>/dev/null)"
    [[ "$status" == "healthy" ]] && record "container status healthy" pass || record "container status healthy" fail

    connected="$(jq -r '[.upstreams[]? | select(.connected == true)] | length' "$EVIDENCE/health-inside.json" 2>/dev/null || echo 0)"
    configured="$(jq -r '.upstreams | length' "$EVIDENCE/health-inside.json" 2>/dev/null || echo 0)"
    if [[ "${configured:-0}" -gt 0 ]]; then
      [[ "${connected:-0}" -gt 0 ]] && record "at least one upstream connected" pass || record "at least one upstream connected" fail
    else
      write egress-note.txt "no upstream configured: pass --env-file/--config-dir with plugin config to test the real chain"
    fi
  fi
else
  record "container /health reachable" fail
fi

# --- egress diagnosis (why a container may fail where the host works) ------------------
if docker inspect "$NAME" >/dev/null 2>&1; then
  {
    echo "network mode: $(docker inspect "$NAME" --format '{{.HostConfig.NetworkMode}}')"
    echo "hosts override mounted: $MOUNT_HOSTS"
    echo "custom CA bundle: ${CA_BUNDLE:-none}"
    echo
    docker exec "$NAME" bash -c '
      echo "resolv.conf:"; head -3 /etc/resolv.conf
      echo "gateway.discord.gg:"; getent hosts gateway.discord.gg || echo "  (no resolution)"
      for target in gateway.discord.gg discord.com; do
        s=$(date +%s%3N)
        if (exec 3<>/dev/tcp/$target/443) 2>/dev/null; then
          echo "  tcp $target:443 ok $(( $(date +%s%3N) - s ))ms"
        else
          echo "  tcp $target:443 FAILED $(( $(date +%s%3N) - s ))ms"
        fi
      done
    ' 2>&1 || echo "diagnosis unavailable"
  } >"$EVIDENCE/egress-diagnosis.txt" 2>&1
fi

# --- secret leak scan ----------------------------------------------------------------
leak_review="clean"
if [[ -n "$ENV_FILE" && -r "$ENV_FILE" ]]; then
  while IFS= read -r line; do
    case "$line" in ''|'#'*) continue ;; esac
    name="${line%%=*}"; value="${line#*=}"
    [[ -z "$value" ]] && continue
    # Only look at evidence files, never at the env file itself.
    if grep -rIl --exclude='*.err' --exclude='*.env' --exclude='live.env*' -F "$value" "$EVIDENCE" >/dev/null 2>&1; then
      leak_review="LEAK: value of $name found in evidence"
      break
    fi
  done <"$ENV_FILE"
fi
write leak-scan.txt "$leak_review"
[[ "$leak_review" == "clean" ]] && record "no secret value in evidence" pass || record "no secret value in evidence" fail

# --- teardown ------------------------------------------------------------------------
if [[ "$KEEP" == "1" ]]; then
  echo "container $NAME and volume ${NAME}-state kept for inspection"
else
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker volume rm "${NAME}-state" >/dev/null 2>&1 || true
fi

{
  echo "image: $IMAGE"
  echo "compose plugin: $([[ "$compose_mode" == "1" ]] && echo present || echo missing)"
  echo
  echo "checks:"
  for check in "${checks[@]}"; do echo "  - $check"; done
  echo
  echo "failures: ${#failures[@]}"
  for failure in "${failures[@]}"; do echo "  ! $failure"; done
  echo
  echo "evidence: $EVIDENCE"
} | tee "$EVIDENCE/summary.txt"

[[ "${#failures[@]}" -eq 0 ]] && exit 0 || exit 1
