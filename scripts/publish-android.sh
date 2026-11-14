#!/usr/bin/env bash
# Publishes the Android head. Requires the .NET Android workload and an Android SDK.
# Usage: ./scripts/publish-android.sh
# Configure Release signing through the standard MSBuild properties
# (AndroidKeyStore, AndroidSigningKeyStore, AndroidSigningStorePass, ...).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_DIR="${OUTPUT_DIR:-$ROOT/artifacts/android}"

dotnet publish "$ROOT/src/Natter.Hosting.Android/Natter.Hosting.Android.csproj" \
  -c "$CONFIGURATION" \
  -f net10.0-android \
  -p:AndroidPackageFormat=apk \
  -o "$OUTPUT_DIR"

echo
echo "Android package(s) in $OUTPUT_DIR"
