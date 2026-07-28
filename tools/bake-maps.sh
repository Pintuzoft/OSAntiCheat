#!/usr/bin/env bash
# Bakes CS2FOW .bvh8 visibility files from the game server's own map VPKs — the only
# reliable source for workshop maps (some, like de_nache, are no longer public).
# Run ON the game server (linux x86-64 with AVX). Idempotent: existing bakes are kept
# unless --force; the analysis side re-verifies source CRCs anyway.
#
# usage: bake-maps.sh <server-root> <output-dir> <map>... [--force]
#   e.g. bake-maps.sh /home/cs2 ./bakes de_cbble_csgo de_zoo de_kismayo de_nache
#
# Downloads the CS2FOW release (pinned below) into ./cs2fow-baker/ on first run, or set
# CS2FOW_DIR to an existing unpacked release.
set -u

CS2FOW_VERSION="0.3.1"
CS2FOW_ZIP="cs2fow-${CS2FOW_VERSION}-linux-x86_64.zip"
CS2FOW_URL="https://github.com/karola3vax/CS2FOW/releases/download/v${CS2FOW_VERSION}/${CS2FOW_ZIP}"

if [ $# -lt 3 ]; then
    grep '^#' "$0" | head -12 >&2
    exit 2
fi

server_root=$1
output_dir=$2
shift 2
force=0
maps=()
for arg in "$@"; do
    if [ "$arg" = "--force" ]; then force=1; else maps+=("$arg"); fi
done

baker_dir=${CS2FOW_DIR:-./cs2fow-baker}
if [ ! -x "$baker_dir/tools/cs2fow_baker" ]; then
    echo "== fetching CS2FOW ${CS2FOW_VERSION} baker into $baker_dir"
    mkdir -p "$baker_dir"
    curl -fL -o "$baker_dir/$CS2FOW_ZIP" "$CS2FOW_URL" || exit 1
    unzip -o -q "$baker_dir/$CS2FOW_ZIP" -d "$baker_dir" || exit 1
    chmod +x "$baker_dir/tools/cs2fow_baker" "$baker_dir/tools/vrf/linux64/Source2Viewer-CLI"
fi

mkdir -p "$output_dir"
ok=0; skipped=0; failed=()
for map in "${maps[@]}"; do
    if [ -f "$output_dir/$map.bvh8" ] && [ "$force" = 0 ]; then
        echo "== $map: bake exists, skipping (--force to rebake)"
        skipped=$((skipped + 1))
        continue
    fi
    vpk=$(find "$server_root" -name "$map.vpk" -not -path "*/backup/*" 2>/dev/null | head -1)
    if [ -z "$vpk" ]; then
        echo "== $map: NO VPK FOUND under $server_root"
        failed+=("$map (vpk not found)")
        continue
    fi
    echo "== $map: baking from $vpk"
    if "$baker_dir/tools/cs2fow_baker" \
        --vpk "$vpk" \
        --vrf "$baker_dir/tools/vrf/linux64/Source2Viewer-CLI" \
        --output "$output_dir" \
        --low-priority; then
        ok=$((ok + 1))
    else
        echo "== $map: BAKE FAILED"
        failed+=("$map (baker error)")
    fi
done

echo
echo "== done: $ok baked, $skipped skipped, ${#failed[@]} failed"
for f in "${failed[@]:-}"; do [ -n "$f" ] && echo "   FAILED: $f"; done
[ ${#failed[@]} -eq 0 ]
