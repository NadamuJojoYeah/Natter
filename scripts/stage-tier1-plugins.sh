#!/usr/bin/env bash
# Stages the NPS business plugins next to an Natter release.
#
# Tier 1 (default): Discord transport + DeepSeek provider — required for a usable host.
# Tier 2 (--tier2): DingTalk transport + Spark provider — best-effort, optional.
#
# Usage:
#   ./scripts/stage-tier1-plugins.sh -o artifacts/live/plugins
#   ./scripts/stage-tier1-plugins.sh -o /opt/natter/plugins -c Release
#   ./scripts/stage-tier1-plugins.sh -o /opt/natter/plugins --tier2
#   ./scripts/stage-tier1-plugins.sh -o /opt/natter/plugins --skip-build --json
#   ./scripts/stage-tier1-plugins.sh -o /opt/natter/plugins --host-dir /opt/natter
#
# Layout written (one directory per plugin, exactly what the NPS loader expects):
#   <plugin-root>/discord/
#   ├── plugin.json
#   ├── config.schema.json
#   ├── lib/<entry>.dll + deps.json + private dependencies
#   └── config.json            # NOT written here — deployment provides it
#
# Guarantees:
#   * never writes config.json and never touches a running host;
#   * never reads or writes secrets;
#   * existing config.json inside a plugin directory is preserved on re-stage;
#   * fails loudly when lib/<entry>.deps.json is missing, so a broken image cannot ship;
#   * with --host-dir, fails loudly (exit 8, listing every file) when a plugin's private copy of a
#     Natter.*.dll differs from the host's copy.
#
# Why --host-dir matters:
#   Each plugin keeps a private copy of the host assemblies it links against (Natter.Chat.dll,
#   Natter.Abstractions.dll, ...). Mixing an old plugin lib with a new host compiles fine but throws
#   at runtime, e.g. `MissingMethodException: Natter.Chat.UpstreamHealth..ctor(...)` which surfaces
#   as HTTP 500 on /health. Always stage plugins from the same build as the host, and let this
#   script prove it.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
PLUGIN_ROOT=""
TIER2=0
SKIP_BUILD=0
HOST_DIR=""
JSON=0

# name|project relative path|directory name|expected plugin id
PLUGIN_TABLE_TIER1=(
  "discord|samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj|discord|natter.transport.discord"
  "deepseek|src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj|deepseek|natter.feature.ai.deepseek"
)
PLUGIN_TABLE_TIER2=(
  "dingtalk|samples/Natter.Sample.Plugin.DingTalk/Natter.Sample.Plugin.DingTalk.csproj|dingtalk|natter.transport.dingtalk"
  "spark|src/Natter.Feature.Ai.Spark/Natter.Feature.Ai.Spark.csproj|spark|natter.feature.ai.spark"
)

usage() {
  sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-2}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -o|--output) PLUGIN_ROOT="${2:-}"; shift 2 ;;
    -c|--configuration) CONFIGURATION="${2:-}"; shift 2 ;;
    --tier2) TIER2=1; shift ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --host-dir) HOST_DIR="${2:-}"; shift 2 ;;
    --json) JSON=1; shift ;;
    -h|--help) usage 0 ;;
    *) echo "unknown argument: $1" >&2; usage 2 ;;
  esac
done

[[ -n "$PLUGIN_ROOT" ]] || { echo "missing -o/--output <plugin-root>" >&2; usage 2; }

if [[ -n "$HOST_DIR" ]]; then
  [[ "$HOST_DIR" == /* ]] || HOST_DIR="$PWD/$HOST_DIR"
  [[ -d "$HOST_DIR" ]] || { echo "--host-dir is not a directory: $HOST_DIR" >&2; exit 3; }
  # Fail before building/staging anything when the directory cannot prove parity at all.
  shopt -s nullglob
  host_probe=("$HOST_DIR"/Natter.*.dll)
  shopt -u nullglob
  if [[ ${#host_probe[@]} -eq 0 ]]; then
    echo "no Natter.*.dll in $HOST_DIR — point --host-dir at the host publish directory (where natter.dll lives)" >&2
    exit 3
  fi
fi

# Resolve a relative output path against the caller's cwd, then create it.
if [[ "$PLUGIN_ROOT" != /* ]]; then
  PLUGIN_ROOT="$PWD/$PLUGIN_ROOT"
fi
mkdir -p "$PLUGIN_ROOT"

entries=("${PLUGIN_TABLE_TIER1[@]}")
if [[ "$TIER2" == "1" ]]; then
  entries+=("${PLUGIN_TABLE_TIER2[@]}")
fi

declare -a staged_names=() staged_ids=() staged_paths=() staged_files=()
declare -a parity_mismatches=()   # "plugin|file|pluginSha|hostSha"
declare -a parity_skipped=()      # plugin names not deployed because of a mismatch
host_parity_compared=0

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    echo "unavailable"
  fi
}

# Compares the plugin's private Natter.*.dll copies with the host's, and appends every mismatch.
# Returns the number of files compared through the global counter.
verify_host_parity() {
  local name="$1" lib_dir="$2"
  local file base host_file plugin_sha host_sha
  for file in "$lib_dir"/Natter.*.dll; do
    [[ -e "$file" ]] || continue
    base="$(basename "$file")"
    host_file="$HOST_DIR/$base"
    # Assemblies that only exist on one side are not comparable (plugin-only or host-only types).
    [[ -f "$host_file" ]] || continue
    plugin_sha="$(sha256_of "$file")"
    host_sha="$(sha256_of "$host_file")"
    host_parity_compared=$((host_parity_compared + 1))
    if [[ "$plugin_sha" != "$host_sha" ]]; then
      parity_mismatches+=("$name|$base|$plugin_sha|$host_sha")
    fi
  done
}

stage_one() {
  local name="$1" project="$2" dirname="$3" expected_id="$4"
  local project_path bin_dir source_dir
  project_path="$ROOT/$project"
  bin_dir="$ROOT/$(dirname "$project")/bin/$CONFIGURATION/net10.0"
  source_dir="$ROOT/$(dirname "$project")"
  local dest="$PLUGIN_ROOT/$dirname"
  local assembly=""

  [[ -f "$project_path" ]] || { echo "project not found: $project_path" >&2; exit 3; }

  if [[ "$SKIP_BUILD" != "1" ]]; then
    echo "== building $name ($CONFIGURATION) ==" >&2
    dotnet build "$project_path" -c "$CONFIGURATION" -v q --nologo >&2
  fi

  assembly="$(basename "${project%.csproj}")"
  if [[ ! -f "$bin_dir/lib/$assembly.dll" ]]; then
    echo "missing build output: $bin_dir/lib/$assembly.dll (build without --skip-build?)" >&2
    exit 4
  fi
  if [[ ! -f "$bin_dir/lib/$assembly.deps.json" ]]; then
    echo "missing dependency graph: $bin_dir/lib/$assembly.deps.json" >&2
    echo "NPS requires lib/<assembly>.deps.json so AssemblyDependencyResolver can resolve it." >&2
    exit 5
  fi
  [[ -f "$source_dir/plugin.json" ]] || { echo "missing plugin.json: $source_dir" >&2; exit 6; }
  [[ -f "$source_dir/config.schema.json" ]] || { echo "missing config.schema.json: $source_dir" >&2; exit 6; }

  # Validate the manifest id before touching the target directory.
  local manifest_id
  manifest_id="$(sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$source_dir/plugin.json" | head -1)"
  if [[ "$manifest_id" != "$expected_id" ]]; then
    echo "plugin id mismatch for $name: manifest='$manifest_id' expected='$expected_id'" >&2
    exit 7
  fi

  # Stage into a temp dir first so a failed copy cannot leave a half-written plugin.
  local tmp="$dest.staging.$$"
  rm -rf "$tmp"
  mkdir -p "$tmp/lib"
  cp -a "$bin_dir/lib/." "$tmp/lib/"
  cp -f "$source_dir/plugin.json" "$source_dir/config.schema.json" "$tmp/"

  # Preserve a deployment-provided config.json across re-staging.
  if [[ -f "$dest/config.json" ]]; then
    cp -f "$dest/config.json" "$tmp/config.json"
  fi

  if [[ -n "$HOST_DIR" ]]; then
    local before=${#parity_mismatches[@]}
    verify_host_parity "$name" "$tmp/lib"
    if [[ ${#parity_mismatches[@]} -gt "$before" ]]; then
      rm -rf "$tmp"
      parity_skipped+=("$name")
      echo "SKIP $name: lib/ does not match $HOST_DIR (old plugin + new host would throw MissingMethodException)" >&2
      return 0
    fi
  fi

  rm -rf "$dest"
  mv "$tmp" "$dest"

  local count
  count="$(find "$dest/lib" -maxdepth 1 -type f | wc -l | tr -d ' ')"
  staged_names+=("$name")
  staged_ids+=("$manifest_id")
  staged_paths+=("$dest")
  staged_files+=("$count")
  echo "staged: $dest ($count files in lib/)" >&2
}

for entry in "${entries[@]}"; do
  IFS='|' read -r name project dirname expected_id <<<"$entry"
  stage_one "$name" "$project" "$dirname" "$expected_id"
done

if [[ -n "$HOST_DIR" && "$host_parity_compared" -eq 0 ]]; then
  echo "no comparable Natter.*.dll found in $HOST_DIR — is --host-dir the publish directory of the host?" >&2
  exit 3
fi

if [[ ${#parity_mismatches[@]} -gt 0 ]]; then
  {
    echo "host/plugin assembly mismatch — refusing to hand out an inconsistent plugin root"
    echo "host dir: $HOST_DIR"
    echo
    printf '  %-10s %-34s %-16s %s\n' "plugin" "assembly" "plugin-sha256" "host-sha256"
    for line in "${parity_mismatches[@]}"; do
      IFS='|' read -r plugin_name file_name plugin_sha host_sha <<<"$line"
      printf '  %-10s %-34s %-16s %s\n' "$plugin_name" "$file_name" "${plugin_sha:0:16}" "${host_sha:0:16}"
    done
    echo
    echo "fix: rebuild the plugins and the host from the same commit, then re-run:"
    echo "  dotnet publish src/Natter.Cli/Natter.Cli.csproj -c $CONFIGURATION -o <host-dir>"
    tier2_flag=""
    [[ "$TIER2" == "1" ]] && tier2_flag=" --tier2"
    echo "  $0 -o $PLUGIN_ROOT -c $CONFIGURATION${tier2_flag} --host-dir $HOST_DIR"
    if [[ ${#parity_skipped[@]} -gt 0 ]]; then
      echo "not deployed: ${parity_skipped[*]}"
    fi
  } >&2
  exit 8
fi

# Machine-readable summary for CI/build logs (no secrets, no dependency on python).
json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

summary_path="$PLUGIN_ROOT/staging-manifest.json"
{
  printf '{\n'
  printf '  "pluginRoot": "%s",\n' "$(json_escape "$PLUGIN_ROOT")"
  printf '  "configuration": "%s",\n' "$(json_escape "$CONFIGURATION")"
  printf '  "tier2": %s,\n' "$([[ "$TIER2" == "1" ]] && echo true || echo false)"
  printf '  "stagedAt": "%s",\n' "$(date -Is)"
  printf '  "hostParity": {"checked": %s, "hostDir": "%s", "comparedFiles": %s, "mismatches": 0},\n' \
    "$([[ -n "$HOST_DIR" ]] && echo true || echo false)" \
    "$(json_escape "$HOST_DIR")" "$host_parity_compared"
  printf '  "plugins": [\n'
  last=$(( ${#staged_names[@]} - 1 ))
  for i in "${!staged_names[@]}"; do
    sep=","
    [[ "$i" == "$last" ]] && sep=""
    printf '    {"name": "%s", "id": "%s", "directory": "%s", "libFiles": %s}%s\n' \
      "$(json_escape "${staged_names[$i]}")" \
      "$(json_escape "${staged_ids[$i]}")" \
      "$(json_escape "${staged_paths[$i]}")" \
      "${staged_files[$i]}" "$sep"
  done
  printf '  ]\n}\n'
} >"$summary_path"
chmod 644 "$summary_path"

if [[ "$JSON" == "1" ]]; then
  cat "$summary_path"
else
  cat <<EOF
plugin root: $PLUGIN_ROOT
manifest   : $summary_path
staged     : ${staged_names[*]}
host parity: $([[ -n "$HOST_DIR" ]] && echo "verified against $HOST_DIR ($host_parity_compared assemblies)" || echo "skipped (pass --host-dir \<host publish dir\> to verify)")
next:
  1. provide secrets through the host secret provider (env / live.env), never in config.json
  2. copy config.sample.json (or the deployment config) to <plugin>/config.json
  3. verify without starting upstreams:
       natter --plugin-dir "$PLUGIN_ROOT" plugins inspect "$PLUGIN_ROOT/discord"
EOF
fi
