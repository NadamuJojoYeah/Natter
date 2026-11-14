#!/usr/bin/env bash
# Builds the desktop host and the test suite. The Android head is opt-in because it
# needs the android workload: INCLUDE_ANDROID=1 ./scripts/build.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Debug}"

dotnet build "$ROOT/src/Natter.Cli/Natter.Cli.csproj" -c "$CONFIGURATION"
dotnet build "$ROOT/tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj" -c "$CONFIGURATION"

if [ "${INCLUDE_ANDROID:-0}" = "1" ]; then
  dotnet build "$ROOT/src/Natter.Hosting.Android/Natter.Hosting.Android.csproj" -c "$CONFIGURATION"
fi
