#!/usr/bin/env bash
# Unified build script for TaskBus MDK² monorepo
# Builds all services and apps, then copies output DLLs to ./build/output/
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUTPUT_DIR="$ROOT_DIR/build/output"

echo "=== TaskBus MDK² Build ==="
echo "Root: $ROOT_DIR"
echo "Output: $OUTPUT_DIR"
echo

# Check Space Engineers assemblies
if [ -z "${SPACE_ENGINEERS_BIN64:-}" ]; then
  echo "Warning: SPACE_ENGINEERS_BIN64 not set, using default path"
fi

mkdir -p "$OUTPUT_DIR"

# List of projects to build
PROJECTS=(
  "services/broker/broker.csproj"
  "services/consumer/consumer.csproj"
  "apps/amn/amn.csproj"
  "apps/sample/sample.csproj"
)

for proj in "${PROJECTS[@]}"; do
  proj_path="$ROOT_DIR/$proj"
  if [ ! -f "$proj_path" ]; then
    echo "Warning: $proj_path not found, skipping"
    continue
  fi
  name=$(basename "$proj" .csproj)
  echo "--- Building $name ---"
  dotnet build "$proj_path" -c Release -o "$OUTPUT_DIR" 2>&1 | tail -3
  echo
done

echo "=== Build complete ==="
echo "Output DLLs in: $OUTPUT_DIR"
ls -la "$OUTPUT_DIR"/*.dll 2>/dev/null || echo "(no DLLs found)"