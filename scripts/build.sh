#!/usr/bin/env bash
# Build the plugin (and run tests unless --no-test). Usage: build.sh [Debug|Release] [--no-test]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="Release"
RUN_TESTS=1

for arg in "$@"; do
  case "$arg" in
    Debug|Release) CONFIG="$arg" ;;
    --no-test)     RUN_TESTS=0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

echo "==> Building OSAntiCheat ($CONFIG)"
dotnet build "$ROOT/src/OSAntiCheat.csproj" -c "$CONFIG"

if [[ "$RUN_TESTS" -eq 1 ]]; then
  echo "==> Running tests"
  dotnet test "$ROOT/tests/OSAntiCheat.Tests.csproj" -c "$CONFIG"
fi

echo "Done."
