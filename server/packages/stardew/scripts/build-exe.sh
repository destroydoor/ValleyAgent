#!/usr/bin/env bash
set -euo pipefail

# Build valley-ai-server.exe using Bun compile
# Output: packages/stardew/bin/valley-ai-server.exe

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_DIR="$(dirname "$SCRIPT_DIR")"
ENTRY="$PKG_DIR/src/cli.ts"
OUTPUT="$PKG_DIR/bin/valley-ai-server.exe"

echo "Building valley-ai-server.exe from $ENTRY..."
mkdir -p "$PKG_DIR/bin"

bun build --compile --target=bun-windows-x64 "$ENTRY" --outfile "$OUTPUT"

echo "Built: $OUTPUT"
ls -lh "$OUTPUT"
