#!/usr/bin/env bash
# Runs the framework test suite.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Debug}"

dotnet test "$ROOT/tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj" \
  -c "$CONFIGURATION" \
  --logger "console;verbosity=normal"
