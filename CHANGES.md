# Changes

Version history for OSAntiCheat, newest first. Every release gets an entry here; the README
describes the current state only. Player/admin names follow the pseudonym scheme from
[TODO.md](TODO.md) (Cn = typed cheater, Gn = griefer, Rn = regular, An = admin).

## v0.9.4 — geometric LOS gate for wallhack.track (CS2FOW BVH8 bakes)

`src/Visibility/` — C# port of CS2FOW's `.bvh8` reader and segment raycaster (MIT, pinned to
format v3/recipe 1, differentially verified bit-identical vs upstream on 400k segments). At
map start the plugin background-loads `BakesDir/<map>.bvh8`; with `WallhackGeoGate` on, a
wallhack.track candidate must be provably occluded by static geometry (6-point body sampling)
AND unspotted by the observer's entire team. A gated signal on a *silent* enemy (≤120 u/s, no
footsteps — no legitimate information channel at all) gets a confidence boost, never a gate.
Missing/stale/invalid bake ⇒ geo gating inactive, spotted-only behaviour unchanged — never off.

Measured before building (21 demos, 313 sessions, 16 maps — TODO.md "GEO-GATE-EXPERIMENTET"):
legit noise 65→15 sessions (max 0.11 sig/min), best-sampled banned cheater kept at 0.47 = 4.3×
the highest legit; a ≥0.2/min + ≥4 alive-min rule flags exactly that cheater population-wide.
Gate defaults OFF pending live validation. Ships with `tools/VisOracle` (bake inspect/query),
`tools/bake-maps.sh` (incremental server-side baking + per-CRC archive + era index), and a
geometry-aware `tools/Sweep` (four eval arms, `--geo-dump`). Bakes are Valve-derived runtime
data (CS2FOW DATA_NOTICE) — gitignored, distributed alongside the demo archive, never in git.

## v0.9.3 — announced version read from the assembly

`ModuleVersion` was a hardcoded string that had survived two releases: a correctly deployed
0.9.2 introduced itself as 0.7.0 in `css_plugins` and the logs. Now derived from `<Version>`
in the csproj via the assembly, so the announced version can never drift from the release.
Content otherwise identical to v0.9.2.

## v0.9.2 — three new logic-breach axes: snap, silent aim, anti-aim

Released as 0.9.2 rather than 0.10.0: the updater compares versions lexically, where
`"0.10.0" < "0.7.0"`. Stay below 0.10 until the comparator is semver-aware.

- **aimbot.snap** live — ≥5° off a head one tick before the shot → ≤0.05° on its *centre* at
  the shot. Archive-validated: 0 of 318k kills and 0 per-shot events in two years of demos show
  the conjunction; the human tails end at 4.53° approach / 0.153° landing. Runs on every shot
  including mid-spray (the classic spray-aimbot pulls exactly one bullet onto the head, where
  burst-gated detectors never look).
- **aimbot.silent** live — a bullet *registers damage* while the shooter's replicated view
  points ≥10° away from every position the victim held in the ~250 ms lag-comp window. The two
  honest paths to an off-view hit are excluded by construction: lag compensation (error =
  minimum over the victim's position history) and recoil compensation (first-of-burst bullets
  only). The 10° floor was read off a 21-demo hurts sweep: the honest burst-opener tail ends at
  exactly 8.0° (0 of 3,486 ≥10°). Both known archetypes — spin-silent (C5) and frozen-view
  psilent (C6) — fire on the banned players' demos; zero false positives across the sweep.
- **antiaim** live — pitch past the engine's ±89° clamp (the honest population parks at
  *exactly* 89.00 — the gate is a free tripwire for anything bypassing the clamp), or ≥6
  consecutive sign-alternating ≥45°/tick yaw reversals (honest maximum measured: one).
  Independent of spinbot: monotonic rotation never alternates.
- **LogPath fix** — a relative path now resolves to `counterstrikesharp/logs/` next to the
  plugin instead of the server's cwd (the v0.7.0 deploy bug). Absolute paths honoured as-is.
- **Flashbang filter** — flash pops no longer count as bullet hits (the weapon string slipped a
  `grenade` substring filter for six archives and owned the entire false tail of the silent-aim
  measurement).
- Config auto-fills to v17 (`EnableSnap`, `EnableSilentAim`, `EnableAntiAim` + knobs). Note:
  CounterStrikeSharp does not rewrite an existing config file — missing keys simply load as
  defaults; move the json away and reload to regenerate it.

## v0.8.0–v0.9.0 — the banned-player forensics that built the new detectors

Replaying every kill and every shot by the server's nine banned players against the 321k-kill
archive population turned tiny samples into verdicts (binomial tails: 6 kills suffice when each
is a 1-in-600 event):

- **Silent aim discovered as a measurable axis** — headshots registering while the shooter's
  replicated view provably pointed elsewhere. Two banned players typed this way
  (*P* ≈ 2×10⁻¹⁷ spin-silent, 1.5×10⁻¹² frozen-view psilent).
- **Four "cheat" bans turned out to be griefers** (teamknifing, teamkills, chat spam — proven
  from demo event logs via the new `tools/DemoInspect`). Ban-list "Other" reasons are ~2/6
  cheat; every label is verified per case before it trains anything.
- **aimbot.snap validated on the archive**: a true-zero baseline for the off→exact conjunction
  across two years of demos.
- **Anti-aim measured before gating** (`AAScan`): honest pitch parks at exactly the engine
  clamp; honest yaw-direction reversals never chain. Both gates sit on measured zeros.

## v0.7.0 — first live deployment

Ran on the real server for a full evening. The headline lesson: an older build that fused
*every* axis (including the falsified ones) alerted on ~80 players — the entire server base;
the shadow-gated build alerted on zero. When a detector flags everyone it measures "played CS
tonight", not cheating. Also: spinbot/bone-lock/anti-recoil live with two response tiers,
dry-run action policy, shadow mode, and tick+map+wallClock stamped on every signal.

## v0.6.1 — null test scored as a McNemar z

Live data exposed two flaws in the raw present-minus-past excess (noise at low counts; the
accumulated score re-measured playtime). Replaced with a McNemar z over discordant samples:
skill cancels, it is self-calibrating, and no amount of playtime moves a null player off z≈0.

## v0.6.0 — the null test goes live

Replaying 11 demos over the server's own ban list found the tracking detector fires on the
*regulars* (they are the ones scanning), while the null test — present-position hits minus
1.5s-past-position hits on unspotted enemies — ranked the verified cheaters 1st, 2nd and 8th
of 70. Promoted to a live detector (`wallhack.nulltest`).

## v0.5.0 — first calibrated release

`wallhack.track` defaults read off a parameter sweep against real demos containing three
admin-banned cheaters alongside their matches' legit players. Cheaters 0.68–1.23 signals per
alive-minute vs the highest of 133 legit sessions at 0.21 (legit baseline 0.026/min — 30×
separation). Honest limits stated in the README of that era: config selected on those three
cheaters, all likely running the same cheat.
