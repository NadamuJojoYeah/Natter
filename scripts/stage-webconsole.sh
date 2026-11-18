#!/usr/bin/env bash
# Stages the WebConsole plugin into an NPS plugin root.
#
#   ./scripts/stage-webconsole.sh artifacts/live/plugins
#   CONFIGURATION=Debug ./scripts/stage-webconsole.sh /srv/natter/plugins
#
# Layout written:
#   <root>/natter.plugin.webconsole/
#   ├── plugin.json
#   ├── config.schema.json
#   ├── config.sample.json
#   ├── lib/Natter.Plugin.WebConsole.dll
#   └── wwwroot/{index.html,app.js,app.css}
#
# The script never writes config.json and never touches the running host.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
PLUGIN_ROOT="${1:-}"

if [[ -z "$PLUGIN_ROOT" ]]; then
  echo "usage: $0 <plugin-root>" >&2
  echo "example: $0 artifacts/live/plugins" >&2
  exit 2
fi

SOURCE="$ROOT/plugins/Natter.Plugin.WebConsole"
BIN="$SOURCE/bin/$CONFIGURATION/net10.0"
DEST="$PLUGIN_ROOT/natter.plugin.webconsole"

if [[ ! -f "$BIN/Natter.Plugin.WebConsole.dll" ]]; then
  echo "== building WebConsole plugin ($CONFIGURATION) =="
  dotnet build "$SOURCE/Natter.Plugin.WebConsole.csproj" -c "$CONFIGURATION" -v q --nologo
fi

if [[ ! -f "$BIN/Natter.Plugin.WebConsole.dll" ]]; then
  echo "build output not found: $BIN/Natter.Plugin.WebConsole.dll" >&2
  exit 3
fi

mkdir -p "$PLUGIN_ROOT"
rm -rf "$DEST"
mkdir -p "$DEST/lib"

cp "$BIN/Natter.Plugin.WebConsole.dll" "$DEST/lib/"
if [[ -f "$BIN/Natter.Plugin.WebConsole.pdb" ]]; then
  cp "$BIN/Natter.Plugin.WebConsole.pdb" "$DEST/lib/"
fi

cp "$SOURCE/plugin.json" "$SOURCE/config.schema.json" "$SOURCE/config.sample.json" "$DEST/"
cp -R "$SOURCE/wwwroot" "$DEST/wwwroot"

cat <<EOF
staged: $DEST
next:
  1. provide secret WEBCONSOLE_TOKEN via the host secret provider (env or key store)
  2. copy config.sample.json to $DEST/config.json (or <plugin-config-dir>/natter.plugin.webconsole.json)
  3. start the host with: natter serve --health-port 5099
  4. open http://127.0.0.1:5090/ and run: natter web status --json
EOF
