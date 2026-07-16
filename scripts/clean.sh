#!/usr/bin/env bash
# Remove all build artifacts (bin/, obj/) and the dist/ output.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "==> Cleaning build artifacts under $ROOT"
find "$ROOT" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
rm -rf "$ROOT/dist"
echo "Done."
