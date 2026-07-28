#!/usr/bin/env bash
# Bakes CS2FOW .bvh8 visibility files from the game server's own map data — the only
# reliable source for workshop maps (some, like de_nache, are delisted from Workshop).
# Run ON the game server (linux x86-64 with AVX).
#
# Workshop maps are not <map>.vpk files: each workshop item is a numeric container VPK
# holding maps/<map>.vpk nested inside. This script therefore first indexes every VPK
# under the server root with the baker's --list-maps, then bakes each requested map
# from whichever container holds it. Idempotent: existing bakes are kept unless --force.
#
# usage: bake-maps.sh <server-root> <output-dir> <map>... [--force]
#   e.g. bake-maps.sh /home/cs2 ./bakes de_cbble_csgo de_zoo de_kismayo
#
# Downloads the CS2FOW release (pinned below) into ./cs2fow-baker/ on first run, or set
# CS2FOW_DIR to an existing unpacked release.
set -u

CS2FOW_VERSION="0.3.1"
CS2FOW_ZIP="cs2fow-${CS2FOW_VERSION}-linux-x86_64.zip"
CS2FOW_URL="https://github.com/karola3vax/CS2FOW/releases/download/v${CS2FOW_VERSION}/${CS2FOW_ZIP}"

if [ $# -lt 3 ]; then
    grep '^#' "$0" | head -15 >&2
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
baker="$baker_dir/tools/cs2fow_baker"
vrf="$baker_dir/tools/vrf/linux64/Source2Viewer-CLI"
if [ ! -x "$baker" ]; then
    echo "== fetching CS2FOW ${CS2FOW_VERSION} baker into $baker_dir"
    mkdir -p "$baker_dir"
    curl -fL -o "$baker_dir/$CS2FOW_ZIP" "$CS2FOW_URL" || exit 1
    unzip -o -q "$baker_dir/$CS2FOW_ZIP" -d "$baker_dir" || exit 1
    chmod +x "$baker" "$vrf"
fi

# Index which container VPK holds which map. Multi-part archives (pak01_003.vpk etc.)
# are skipped — only *_dir.vpk carries the directory. Unreadable VPKs are ignored.
echo "== indexing VPKs under $server_root"
declare -A map_to_vpk
while IFS= read -r vpk_file; do
    while IFS= read -r found; do
        [ -n "$found" ] && map_to_vpk[$found]=$vpk_file
    done < <("$baker" --list-maps --vpk "$vpk_file" 2>/dev/null)
done < <(find "$server_root" -name "*.vpk" ! -name "*_[0-9][0-9][0-9].vpk" 2>/dev/null)
echo "   ${#map_to_vpk[@]} maps found in cache"

mkdir -p "$output_dir"
ok=0; skipped=0; failed=()
for map in "${maps[@]}"; do
    if [ -f "$output_dir/$map.bvh8" ] && [ "$force" = 0 ]; then
        echo "== $map: bake exists, skipping (--force to rebake)"
        skipped=$((skipped + 1))
        continue
    fi
    container=${map_to_vpk[$map]:-}
    if [ -z "$container" ]; then
        echo "== $map: NOT FOUND in any VPK under $server_root"
        failed+=("$map (not in cache)")
        continue
    fi
    echo "== $map: baking from $container"
    if "$baker" --game "$server_root" --map "$map" --vpk "$container" \
        --vrf "$vrf" --output "$output_dir/$map.bvh8" --low-priority; then
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
