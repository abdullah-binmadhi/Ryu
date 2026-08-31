#!/usr/bin/env bash
set -e

# ==============================================================================
# Ryu - Native Apple Silicon Release Build & Packaging Script
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$ROOT_DIR/distribution/publish/osx-arm64"
ENTITLEMENTS="$ROOT_DIR/distribution/macos/entitlements.xml"

echo "==> Building Ryu Native ReadyToRun (R2R) Release for osx-arm64..."
mkdir -p "$OUTPUT_DIR"

dotnet publish "$ROOT_DIR/src/Ryujinx.Headless/Ryujinx.Headless.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained \
    -p:PublishReadyToRun=true \
    -p:TieredPGO=true \
    -p:OptimizationPreference=Speed \
    -o "$OUTPUT_DIR"

echo "==> Signing binaries with Hardened Runtime entitlements..."
codesign --entitlements "$ENTITLEMENTS" -f -s - "$OUTPUT_DIR/Ryu"

# If portable environment does not exist in publish output, initialize it once.
# If it already exists, NEVER overwrite it to preserve user save data and settings.
if [ ! -d "$OUTPUT_DIR/portable" ]; then
    echo "==> Initializing portable environment..."
    mkdir -p "$OUTPUT_DIR/portable"
    if [ -d "$ROOT_DIR/src/Ryujinx.Headless/bin/Release/net10.0/portable" ]; then
        cp -R "$ROOT_DIR/src/Ryujinx.Headless/bin/Release/net10.0/portable/"* "$OUTPUT_DIR/portable/"
    fi
else
    echo "==> Preserving existing portable save data and configuration in $OUTPUT_DIR/portable"
fi

echo "=============================================================================="
echo " Ryu Release Build Complete!"
echo " Binary Location: $OUTPUT_DIR/Ryu"
echo "=============================================================================="
