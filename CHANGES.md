# Changes

Version history for OSAntiCheat, newest first. Every release gets an entry here; the README
describes the current state only. Player/admin names follow the pseudonym scheme from
[TODO.md](TODO.md) (Cn = typed cheater, Gn = griefer, Rn = regular, An = admin).

## v0.9.91 — nick-changer detection with auto-kick (name-churn edge)

The live-captured cheater (2026-08-04) ran an **animated marquee nick** — demo-measured 614
renames in ~8.5 minutes (~1.3/s, `m→me→mem→…→memesex` and back) — which also defeats
kick-by-name. Every other player in the demo: zero renames; the 910 logged map-sessions show
honest renames only as isolated events.

- New `NameChangeDetector` (`namechanger`, Behavioural, weight 1.0): counts renames in a rolling
  window; ≥`NameChangeMinChanges` (3) inside `NameChangeWindowSeconds` (20) → signal carrying
  the new **`name-churn`** edge, in `AutoActionEdges` by default → kick. The rate gate is
  unreachable by hand (three deliberate Steam renames take far longer than 20 s) and the marquee
  crosses it in ~2 s. The first non-physics edge — population-measured-zero instead of
  impossible; remove `"name-churn"` from `AutoActionEdges` for fusion-only.
- Default `AutoActionAnnounce` no longer says "input impossible for a human" (untrue for this
  edge — the message must never overclaim): now `[OSAC] CHEAT DETECTED — {name} was kicked
  ({detector})`. **Server configs regenerated at v18 keep the old wording — update the announce
  line manually.**
- Version is 0.9.91 (not 0.9.10) because OSBase compares versions lexically and would treat
  0.9.10 as a downgrade from 0.9.9.

Config version 19. 93 tests.

## v0.9.9 — enforcement messaging made unmistakable

Owner feedback on v0.9.8 before it ever fired: the messages must be impossible to misread —
especially for admins. Changes, all messaging:

- **Admins always get a two-line chat notice on every auto-action**, regardless of
  `NotifyAdminsInChat` (that flag gates fusion *suspicions*; an action the plugin took on
  their server is never something admins should have to discover in a log file). Line 1:
  `CHEAT KICKED: <name> — SPINBOT (steamid …)` — everything needed to escalate to a permaban.
  Line 2: the raw detector evidence string. Dry-run actions are labelled
  `CHEAT CONFIRMED (dry-run, NOT kicked)`.
- **Blunt defaults for the kick reason and public announce**: `CHEAT DETECTED: {detector}`
  instead of v0.9.8's vague "impossible input signature".
- New placeholders `{detector}` and `{edge}` in `AutoActionCommand`/`AutoActionAnnounce`, and
  the announce now substitutes all placeholders (v0.9.8 only substituted `{name}`).

No detector or threshold changes.

## v0.9.8 — first enforcement: auto-kick on the two deterministic edges

The plugin acts on its own for the first time — deliberately on the narrowest possible slice.
Signals can now carry a deterministic **edge** marker, set only where the signature is
physically impossible for a real client *and* measured-zero on the archive:

- **`spin-hs-kill`** (SpinbotDetector.OnKill): headshot kill mid-spin — >360° continuous
  rotation still whirling ≥1200°/s at the kill tick — repeated (`SpinbotMinSpinHsKills`,
  new knob, default 2: the first is a fluke-guard, so a spinbot buys exactly two kills).
- **`fake-pitch`** (AntiAimDetector): pitch past the engine's server-side ±89° clamp for 3+
  consecutive ticks. Fires without needing any kill at all.

A signal with an edge in `AutoActionEdges` runs `AutoActionCommand` (default
`kickid {userid} …`) and `AutoActionAnnounce` in public chat. **Armed by default**
(`AutoActionEnabled=true`) — the validation the old dry-run flag was waiting for has happened:
0 events across 321k archive kills and the whole live deployment window. The response is a
kick, not a ban: a bug costs a reconnect, escalation to `css_ban` is a config choice. Every
action decision, executed or dry-run, is durably logged as a `type:"action"` JSON row.

Replaces the never-armed v0.2-era `AutoActionSpinbot`/`SpinbotActionCommand` path, which
covered only the spin edge and double-logged its signal. Poll-based continuous-spin and yaw
jitter intentionally carry no edge (fusion/corroboration only), bots are never acted on, and
shadowed detectors cannot reach the action path. Config version 17 → 18; 90 tests pass
(edge markings are regression-locked).

## v0.9.7 — null test recalibrated against the first live-caught labelled cheater

The live pipeline caught its first in-the-act positive (2026-08-04): **C7**, a self-admitted
cheat user ("i use strafe.one crack" in chat), joined mid-map, teamknifed on arrival, renamed
every round to dodge kick-by-name, and one-tapped 16 headshot kills of 17 with the revolver in
4.3 alive-minutes. First signal ~1 minute after he joined; 9 `wallhack.track` + 10
`wallhack.nulltest` signals in shadow. No mechanical Tier-1 event — his lag-window aim error
sits under 1°, i.e. "legit"-style aim assist plus wall information. The information axis is
the whole case, which is exactly what it exists for.

Reading 5 days of post-v0.9.5 population shadow data against him exposed two null-test
calibration errors, both now fixed by measurement:

- **`NullTestMinObservations` 30 → 400.** The 20 Hz polls are autocorrelated — one engagement
  produces an unbroken run of present-only discordant samples, so McNemar's independence
  assumption fails at small n: legit regulars hit 97–100% present-rate on 30-sample windows
  and z≈9 inside 20 seconds. At 400 the burst noise has washed out; C7 still passed 400
  discordant samples within ~4 minutes of joining, so the gate costs little latency.
- **New `NullTestWeight`, default 0.5.** Even at large n the present-bias is universally
  *positive* for skilled players (sound + game sense aim you where unseen enemies are): on
  large-n excess over the map population, C7 ranked only 4th–6th behind known regulars. The
  axis corroborates — it must never reach Review alone.

Validation: replaying the fusion engine over the whole 5-day live log, the previous defaults
put **97** player-map-sessions at Review (96 of them known-legit regulars; C7 ranked 4th).
The new defaults put exactly **one** session at Review — C7, simulated peak 3.09 — with the
runner-up legit session at 2.4× lower score. Caveat honestly stated: the positive class is
n=1 and the knobs were chosen on this window; the negative-class evidence (96 false Reviews
eliminated across dozens of regulars) is what carries the change. `wallhack.track` separated
on both paths independently: 2.1 signals/min for C7 vs ~0.1/min field maximum in full
sessions. Both axes stay shadowed — this release makes the null test *eligible* to graduate,
it does not graduate it.

## v0.9.6 — bake-maps.sh ships in the release zip

The server-side baker wrapper now rides the plugin's own deploy pipeline: the release zip
gains `OSAntiCheat/bake-maps.sh`, so an OSBase-managed install always carries the tool that
produces the bakes its geo gate consumes — no separate distribution channel to keep in sync
(the server's bake cron was being automated via Puppet, and the plugin pipeline already
existed). The script is inert in `plugins/` (CSSharp only loads dlls). Point the cron at it
with the working/output dir OUTSIDE the plugin folder — that folder is replaced on every
update, bakes and the baker download must live elsewhere:

```
0 6 * * * cd /home/cs2/osanticheat && bash .../counterstrikesharp/plugins/OSAntiCheat/bake-maps.sh <server-root> ./bakes --all >> bake.log 2>&1
```

Plugin code identical to v0.9.5.

## v0.9.5 — population-relative null test + measured min-enemy-move gate

Two fixes read straight out of the first accumulated live log after the geo deploy
(2026-07-25→30: 2,366 signals, all of them wallhack.nulltest/track — six live days with zero
Tier-1 events, the measured-zero baselines hold).

- **wallhack.nulltest — per-map population baseline.** The absolute McNemar z proved
  map-dependent: night-variant maps inflate the *whole population* (median z 10.0 and 7.0 vs
  ~5 on every normal map; several regulars simultaneously at z 19–22 in the same session), most
  plausibly because spotted state is unreliable on the dark community remakes — an enemy the
  observer genuinely sees still counts as "unspotted", so everyone "tracks the present". The fix
  is the project's standing principle instead of hardcoded map lists: once the rest of the map's
  population has ≥ `NullTestMinPopObservations` (default 200) discordant samples, emission also
  requires a two-proportion z ≥ `NullTestMinZ` of the player's present-rate **over everyone
  else's**. A map artifact hits both sides equally and cancels; the gate can only ever suppress,
  never add, and with a thin population the absolute test runs alone (pre-v0.9.5 behaviour).
  Evidence is per-map: the detector now resets on map change. Replayed offline against the
  worst live night-map session (pooled population present-rate 0.68 vs ~0.5 normal), the
  baseline silences it.

- **wallhack.track — `WallhackMinEnemyMoveUnits` 0 → 100, measured.** The quiet-incident review
  had already identified the artifact: bearing is observer-relative, so the observer's *own*
  movement sweeps the bearing past a standing enemy and the view "follows" it — 10 of 204 live
  geo signals had enemy movement 0u. Swept 0/25/50/75/100/150/200 over the 21-demo corpus
  (`tools/Sweep --minmove`, GEO+TEAM arm): at 100 the legit sessions with a signal drop 13→10
  (signals 14→11) while **every** cheater signal survives (0.47 and 1.23 sig/min unchanged);
  150 eats a true cheater signal, 200 eats them all. 100 is the knee — exactly where legit
  noise stops improving for free.

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
