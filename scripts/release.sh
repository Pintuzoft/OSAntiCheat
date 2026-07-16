#!/usr/bin/env bash
# Produce a deployable release zip matching the OSBase package-manager convention:
#   * named   OSAntiCheat_v<version>.zip   (matches glob  OSAntiCheat_v0.*\.zip)
#   * contains a single top-level  OSAntiCheat/  folder holding the plugin files,
#     so extracting into  .../counterstrikesharp/plugins/  yields
#     .../plugins/OSAntiCheat/OSAntiCheat.dll
#
# Usage: release.sh [version]   (version defaults to <Version> in the csproj)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$ROOT/src/OSAntiCheat.csproj"
NAME="OSAntiCheat"

# Version: explicit arg wins, else read <Version> from the csproj.
VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$CSPROJ" | head -n1 || true)"
fi
VERSION="${VERSION:-0.0.0}"

echo "==> Releasing $NAME v$VERSION"

# Fresh dist tree. STAGE becomes the OSAntiCheat/ folder at the zip root.
rm -rf "$ROOT/dist"
STAGE="$ROOT/dist/$NAME"
PUBLISH="$ROOT/dist/_publish"
mkdir -p "$STAGE"

# Build a clean publish output.
dotnet publish "$CSPROJ" -c Release -o "$PUBLISH" -p:Version="$VERSION"

# Copy the plugin assets straight into the plugin folder. The CounterStrikeSharp API dll is
# host-provided (ExcludeAssets runtime) so it is not in the output and must not be shipped.
cp "$PUBLISH/$NAME.dll" "$STAGE/"
[[ -f "$PUBLISH/$NAME.deps.json" ]] && cp "$PUBLISH/$NAME.deps.json" "$STAGE/"
[[ -f "$PUBLISH/$NAME.pdb" ]]       && cp "$PUBLISH/$NAME.pdb" "$STAGE/"

# Ship docs inside the plugin folder (keeps plugins/ clean — nothing lands loose).
cp "$ROOT/README.md" "$STAGE/" 2>/dev/null || true
cp "$ROOT/LICENSE"   "$STAGE/" 2>/dev/null || true

# Archive as OSAntiCheat_v<version>.zip. Prefer `zip`, fall back to Python's zipfile.
ARCHIVE="$ROOT/dist/${NAME}_v${VERSION}.zip"
if command -v zip >/dev/null 2>&1; then
  ( cd "$ROOT/dist" && zip -rq "$(basename "$ARCHIVE")" "$NAME" )
else
  python3 - "$ROOT/dist" "$NAME" "$ARCHIVE" <<'PY'
import shutil, sys
base, folder, archive = sys.argv[1], sys.argv[2], sys.argv[3]
shutil.make_archive(archive[:-4], "zip", base, folder)
PY
fi

rm -rf "$PUBLISH"
echo "==> Package: $ARCHIVE"
