# Natter Tier 1 container image.
#
# Build context is the repository root:
#   docker build -f deploy/docker/Dockerfile -t natter:tier1 .
#
# What the image contains:
#   /app            published CLI host (dotnet natter.dll) + samples plugins (greetings, metrics)
#   /app/plugins    Tier 1 NPS plugins staged by scripts/stage-tier1-plugins.sh
#                     discord   natter.transport.discord
#                     deepseek  natter.feature.ai.deepseek
#                   Tier 2 (--build-arg TIER2=true):
#                     dingtalk  natter.transport.dingtalk
#                     spark     natter.feature.ai.spark
#
# The image never contains config.json or secrets: configuration is mounted at /config (read-only)
# and credentials come from the environment/secret provider.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG CONFIGURATION=Release
# Tier 2 plugins are optional; Tier 1 (Discord + DeepSeek) is always staged.
ARG TIER2=false

WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Copy only what the build needs so source-only edits do not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props Natter.slnx ./
COPY build/ build/
COPY src/ src/
COPY samples/ samples/
COPY scripts/stage-tier1-plugins.sh scripts/stage-tier1-plugins.sh

# 1. Publish the host. This also stages the sample plugins (greetings/metrics) into /out/plugins.
RUN dotnet publish src/Natter.Cli/Natter.Cli.csproj \
      -c "$CONFIGURATION" \
      -o /out \
      /p:UseAppHost=false

# 2. Stage the business plugins. Fails the build when:
#      * lib/<entry>.deps.json is missing (the NPS loader could not resolve the plugin), or
#      * a plugin's private copy of a Natter.*.dll differs from the host's copy in /out.
#    `--host-dir /out` turns on the SHA-256 parity check from scripts/stage-tier1-plugins.sh
#    (task-9): a plugin keeps its own copy of the host assemblies it links against, so an old
#    plugin lib + a new host compiles fine but throws MissingMethodException at runtime — which
#    surfaces as HTTP 500 on /health. The image must never ship that mix.
RUN chmod +x scripts/stage-tier1-plugins.sh \
 && if [ "$TIER2" = "true" ]; then \
      scripts/stage-tier1-plugins.sh -o /out/plugins -c "$CONFIGURATION" --tier2 --host-dir /out --json; \
    else \
      scripts/stage-tier1-plugins.sh -o /out/plugins -c "$CONFIGURATION" --host-dir /out --json; \
    fi \
 && echo "== host parity ==" \
 && grep -q '"hostParity": {"checked": true' /out/plugins/staging-manifest.json \
 && grep -q '"mismatches": 0' /out/plugins/staging-manifest.json

# 3. Fail-fast manifest check inside the build (no secrets, no network, no upstream started).
RUN dotnet /out/natter.dll plugins inspect /out/plugins/discord \
 && dotnet /out/natter.dll plugins inspect /out/plugins/deepseek \
 && if [ "$TIER2" = "true" ]; then \
      dotnet /out/natter.dll plugins inspect /out/plugins/dingtalk \
      && dotnet /out/natter.dll plugins inspect /out/plugins/spark; \
    fi


FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# UID/GID 10001: unprivileged, identical on every host so state volume ownership is predictable.
# NOTE: do not name these APP_UID/APP_GID — the mcr .NET 9+ base images export APP_UID=1654 as an
# environment variable, which silently overrides an ARG of the same name and would run the host as
# the base image's "app" user instead of the intended 10001.
ARG NATTER_UID=10001
ARG NATTER_GID=10001

ENV NATTER_CONFIG_DIR=/config \
    NATTER_STATE_DIR=/state \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0

WORKDIR /app

# /state is created and owned here on purpose: Docker copies the image directory's ownership into
# a fresh named volume, which is what lets UID 10001 write the SQLite state without a root step.
RUN set -eux; \
    getent group "$NATTER_GID" >/dev/null || groupadd --gid "$NATTER_GID" natter; \
    getent passwd "$NATTER_UID" >/dev/null || useradd \
      --uid "$NATTER_UID" --gid "$NATTER_GID" --home-dir /app --no-create-home --shell /usr/sbin/nologin natter; \
    mkdir -p /app /config /state; \
    chown -R "$NATTER_UID:$NATTER_GID" /app /state

COPY --from=build --chown=$NATTER_UID:$NATTER_GID /out/ /app/

USER $NATTER_UID:$NATTER_GID

# Loopback only: the health port must never be published to the host or the network.
EXPOSE 5099

# Liveness is "the host answers /health on its loopback port", not "Tier 1 profile is healthy".
# A Tier 1-only deployment reports status=degraded while Tier 2 profiles are absent, which is
# expected and must not flap the container; readiness semantics belong to the operator's alerting
# (see docs/operations/2026-11-21-p4-5-ops-closeout-run.md).
HEALTHCHECK --interval=60s --timeout=10s --start-period=120s --retries=3 \
  CMD ["bash","-c","exec 3<>/dev/tcp/127.0.0.1/5099 || exit 1; printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3; read -r status_line <&3; case \"$status_line\" in *' 200 '*) exit 0;; *) exit 1;; esac"]

ENTRYPOINT ["dotnet", "/app/natter.dll"]
# --health-port 5099 gives the healthcheck a loopback endpoint and lets operators sample the same
# JSON the runbook documents. Override with `command:` to change ports or flags.
CMD ["serve", "--health-port", "5099"]
