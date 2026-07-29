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
#        bake-maps.sh <server-root> <output-dir> --all [--force]   # every map in the cache
#   e.g. bake-maps.sh /home/cs2 ./bakes de_cbble_csgo de_zoo de_kismayo
# Prints per-map bake times and a slowest-first summary at the end.
#
# Incremental: <output-dir>/manifest.tsv records each map's container fingerprint
# (size:mtime); unchanged maps are skipped, so a daily cron re-bakes only what Steam
# updated. Every new bake is also copied to <output-dir>/archive/<map>-<crc>.bvh8 —
# old map versions are never overwritten, which keeps historical demos matchable.
#   0 6 * * * cd /home/cs2/osanticheat && ./bake-maps.sh /home/cs2/serverfiles ./bakes --all >> bake.log 2>&1
#
# archive/ (per-CRC bakes + bakes-history.tsv era index) is meant to be shipped to the same
# host as the demo archive — same transport as the demo upload, with CS2FOW's DATA_NOTICE
# alongside. The game server itself only needs the current bakes in <output-dir>.
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
bake_all=0
maps=()
for arg in "$@"; do
    if [ "$arg" = "--force" ]; then force=1
    elif [ "$arg" = "--all" ]; then bake_all=1
    else maps+=("$arg"); fi
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

if [ "$bake_all" = 1 ]; then
    # Skip 3D-skybox sub-maps and editor/prefab scenes: they list as maps but carry no
    # world physics (instant BAKE FAILED noise). Explicitly named maps bypass the filter.
    maps=()
    while IFS= read -r m; do maps+=("$m"); done < <(printf '%s\n' "${!map_to_vpk[@]}" \
        | grep -viE 'skybox|3d_?sky|^prefabs/|/prefabs/|^editor/|^lobby_|^glass_test$|^dynamic$' | sort)
    echo "== --all: baking ${#maps[@]} maps"
fi

mkdir -p "$output_dir" "$output_dir/archive"
manifest="$output_dir/manifest.tsv"
declare -A fingerprint
[ -f "$manifest" ] && while IFS=$'\t' read -r m fp; do fingerprint[$m]=$fp; done < "$manifest"

ok=0; skipped=0; failed=(); timings=()
total_start=$(date +%s)
for map in "${maps[@]}"; do
    container=${map_to_vpk[$map]:-}
    if [ -z "$container" ]; then
        echo "== $map: NOT FOUND in any VPK under $server_root"
        failed+=("$map (not in cache)")
        continue
    fi
    current_fp=$(stat -c '%s:%Y' "$container")
    if [ -f "$output_dir/$map.bvh8" ] && [ "$force" = 0 ] \
        && [ "${fingerprint[$map]:-}" = "$current_fp" ]; then
        skipped=$((skipped + 1))
        continue
    fi
    echo "== $map: baking from $container"
    map_start=$(date +%s)
    if "$baker" --game "$server_root" --map "$map" --vpk "$container" \
        --vrf "$vrf" --output "$output_dir/$map.bvh8" --low-priority; then
        elapsed=$(($(date +%s) - map_start))
        size=$(du -h "$output_dir/$map.bvh8" | cut -f1)
        echo "   ${elapsed}s, $size"
        timings+=("$elapsed $map")
        fingerprint[$map]=$current_fp
        crc=$("$baker" --inspect-bvh8 "$output_dir/$map.bvh8" \
            | grep -o '"source_crc32":"0x[0-9a-f]*"' | cut -d'"' -f4)
        if [ -n "$crc" ]; then
            cp -n "$output_dir/$map.bvh8" "$output_dir/archive/$map-$crc.bvh8"
            # Era index for the demo-analysis version guard: which bake was live when.
            echo "$(date -u +%F)	$map	$crc" >> "$output_dir/archive/bakes-history.tsv"
        fi
        ok=$((ok + 1))
    else
        echo "== $map: BAKE FAILED"
        failed+=("$map (baker error)")
    fi
done

for m in "${!fingerprint[@]}"; do printf '%s\t%s\n' "$m" "${fingerprint[$m]}"; done | sort > "$manifest"

echo
echo "== done: $ok baked, $skipped skipped, ${#failed[@]} failed, $(($(date +%s) - total_start))s total"
if [ ${#timings[@]} -gt 0 ]; then
    echo "== slowest first:"
    printf '%s\n' "${timings[@]}" | sort -rn | head -20 | while read -r t m; do printf '   %4ss  %s\n' "$t" "$m"; done
fi
for f in "${failed[@]:-}"; do [ -n "$f" ] && echo "   FAILED: $f"; done
[ ${#failed[@]} -eq 0 ]
