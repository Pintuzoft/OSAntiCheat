# Visibility oracle (CS2FOW BVH8 bakes)

`src/Visibility/` answers the question our wallhack detectors previously had to
approximate through CS2's spotted system: **was there an actual line of sight between
two points, or was it blocked by static map geometry?**

It is a C# port of the `.bvh8` reader and segment raycaster from
[CS2FOW](https://github.com/karola3vax/CS2FOW) (MIT), the open source server-side
anti-wallhack plugin. CS2FOW bakes each map's static collision triangles into an
8-wide BVH; we read the same files and run the same query.

## Why the "static geometry only" limitation works in our favor

The bake knows nothing about doors, breakables, props, or smoke. That means:

- **BLOCKED is trustworthy** — a baked wall really blocks sight. This is the
  incriminating direction: a player tracking an enemy through a BLOCKED sightline had
  no legitimate visual.
- **CLEAR is inconclusive** — the sightline may still have been occluded by a door,
  prop, or smoke. Never treat CLEAR as proof a player *did* see someone.

This is CS2FOW's fail-open design mirrored into detection: their uncertainty shows
players; ours declines to flag.

## Usage

```csharp
var map = Bvh8Format.Load("de_dust2.bvh8");           // throws Bvh8FormatException if invalid
var hit = Bvh8Raycaster.SegmentBlocked(map, eyePos, targetPos);
if (hit.Blocked) { /* no static sightline; hit.PacketIndex identifies the wall */ }

// Consecutive queries against a near-stationary pair: pass the previous blocking
// packet to retest it first and usually skip the full tree descent.
hit = Bvh8Raycaster.SegmentBlocked(map, eyePos, targetPos, hit.PacketIndex);
```

The loader enforces the full upstream validation contract (format version 3, bake
recipe 1, exact offsets, single rooted tree, payload CRC). A bake either loads
completely or throws — there is no partially-valid state.

Query endpoints are *excluded* (open segment): a segment starting exactly on a wall
surface is not blocked by that surface. Use eye/hitbox positions, not wall-adjacent
points.

## CLI

```
tools/VisOracle:
  osac-visoracle info  <bake.bvh8>                      # header + validation
  osac-visoracle ray   <bake.bvh8> x1 y1 z1 x2 y2 z2    # BLOCKED/CLEAR
  osac-visoracle bench <bake.bvh8> [queries]            # throughput check
```

## Getting bakes

Official-map bakes are published in CS2FOW's GitHub releases (`maps-v2` at the time
of writing; SHA256SUMS provided). Custom maps can be baked with CS2FOW's own baker.

**Never commit `.bvh8` files** (enforced via `.gitignore`): they are derived from
Valve map data and carry CS2FOW's separate `DATA_NOTICE`, not the MIT license.
Each bake is tied to a specific map version via source CRC; when Valve updates a map,
fetch or bake a matching replacement.

## Fidelity and performance

The port is verified against upstream by differential testing: 200 000 random
segments per map on `de_dust2` and `de_mirage` bakes produce bit-identical results
(same verdict, same blocking packet) between CS2FOW's AVX implementation and this
scalar C# port. Deterministic tests live in `tests/Bvh8Tests.cs`.

Measured on `de_dust2` (404k triangles): ~130 ms load+validate, ~1.6 µs per query
single-threaded. Demo analysis needs at most a few thousand queries per round, so
scalar is more than fast enough; the traversal is structured so a `Vector256`
version can be dropped in if live per-tick use ever demands it.

Pinned upstream: format version 3, bake recipe 1, CS2FOW v0.3.1 (2026-07-23). If
CS2FOW bumps the format, port their loader changes deliberately — the strict version
check means new bakes will be rejected, never misread.
