#!/usr/bin/env bash
# Builds one console target into its plugin wwwroot (task-62/65).
#
#   ./ui/scripts/build-target.sh webconsole|conversation
#
# Called from the plugin csproj targets, so `dotnet build/publish` alone produces a complete plugin.
# Two plugin projects may build in parallel and share ui/node_modules, hence the cross-process lock;
# a half-installed node_modules is detected via the vite shim and repaired with a clean `npm ci`.
set -euo pipefail

TARGET="${1:-}"
case "$TARGET" in
  webconsole|conversation) ;;
  *) echo "usage: $0 webconsole|conversation" >&2; exit 2 ;;
esac

UI_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOCK="$UI_DIR/.ui-build.lock"
NODE_MODULES="$UI_DIR/node_modules"
VITE_SHIM="$NODE_MODULES/.bin/vite"

build() {
  if [[ ! -x "$VITE_SHIM" ]]; then
    echo "[ui] installing dependencies (npm ci)…"
    rm -rf "$NODE_MODULES"
    ( cd "$UI_DIR" && npm ci --no-audit --no-fund )
  fi

  ( cd "$UI_DIR" && npm run "build:$TARGET" )
}

if command -v flock >/dev/null 2>&1; then
  exec 9>"$LOCK"
  flock -w 900 9
  build
else
  build
fi
