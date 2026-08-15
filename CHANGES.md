# Changes

Version history for OSAntiCheat, newest first. Every release gets an entry here; the README
describes the current state only. Player/admin names follow the pseudonym scheme from
[TODO.md](TODO.md) (Cn = typed cheater, Gn = griefer, Rn = regular, An = admin).

## v0.9.104 — the frozen cheat is disarmed

Second finding from the owner's self-freeze drill: MOVETYPE_NONE stops movement, not
weapons — the statue could still shoot (he plinked a bot from mid-air to prove it), and a
frozen wall+aim package sniping for the rest of the round is a punishment in name only.
The freeze now strips all weapons as it lands. The statue is harmless, admins keep the
evidence pair, and the next respawn re-equips as normal.

Config schema unchanged (v24). 121 tests.

## v0.9.103 — the action notice says what actually happened

The owner froze himself testing the airgain chain (thresholds lowered via overlay,
`sv_autobunnyhopping 1` for script-grade re-jump timing — a fine end-to-end drill: whisper →
red alert → edge → freeze announce all landed). The admin evidence line, though, opened with
"CHEAT KICKED" — a verdict hardcoded in the kick era, now plainly false while the frozen
cheat hangs mid-air in front of everyone. The messaging rule says the line must never lie:
the notice now carries the action's own verb — "CHEAT FROZEN" for the freeze, "CHEAT KICKED"
for the command path, and dry-runs spell out what was NOT done.

Config schema unchanged (v24). 121 tests.

## v0.9.102 — the freeze holds for the rest of the round

Ten seconds was the cautious default; the owner's call is harsher and simpler: a confirmed
bunnyhop script stays frozen until the round ends. `AirGainFreezeSeconds` default 10 → −1
(negative = rest of round). No thaw timer exists in that mode — the next respawn's fresh pawn
carries a normal movetype, so the engine itself is the thaw. Until then the mid-air statue is
a free target. Positive values still mean a timed freeze; 0 still routes the edge through the
generic AutoActionCommand path.

NOTE for already-generated v24 configs: the stored `AirGainFreezeSeconds: 10` wins over the
new default — set it to −1 in the overlay (or regenerate) to get rest-of-round.

Config schema unchanged (v24). 121 tests.

## v0.9.101 — airgain recalibrated on the rolling-window statistic (and the clamp trap)

Post-release calibration against the corpus using the EXACT statistic the live detector
evaluates (rolling window, retro-chained burst starters) instead of whole-session medians,
plus a per-hop audit of C9's session. Three fixes fell out:

- **The takeoff clamp was eating the cheater's evidence.** `sv_enablebunnyhopping 0` resets a
  bhopper to ~180 u/s at every takeoff — exactly the detector's old 180 u/s launch floor, which
  silently disqualified half of C9's arcs. Floor lowered to 120: the gain median and the
  over-sprint peak gate carry the discrimination, the floor only filters standstill hop spam.
- **Burst starters count retroactively** (from v0.9.100's follow-up): hop 1 of a chain joins the
  window the moment hop 2 chains onto it, so 3–4-hop burst scripts (C9's live pattern: EVERY
  jump gained +67…+150 u/s, one from a 43 u/s standstill to 193) cannot duck the arc minimum.
- **The whisper moves to the five-arc window.** Honest 4-arc windows reach +33.5 median (one
  lucky downhill run); five-arc windows top out at +21.0 across 124 sessions. AirGainMinArcs
  default 4 → 5; a lone 4-hop burst now proves nothing, two bursts inside 90 s still convict.
  C9 under the final statistic: windowed median +71.1 — 3.4× the honest maximum.

Also measured and REJECTED: the landing-to-rejump timing axis (script = zero variance). Demo
z-inference cannot resolve the gap (every corpus session reads ~1 tick, honest and cheater
alike) — parked until it can be measured live from real OnGround flags, not shipped unvalidated.

Config schema unchanged (v24; only the AirGainMinArcs default changed — a config file already
generated at v24 keeps its stored 4, set it to 5 by hand or regenerate). 121 tests.

## v0.9.100 — movement.airgain: the bunnyhop-script detector, with a mid-air freeze

C9 bunnyhopped straight through a correctly configured server: `sv_autobunnyhopping 0` only
demands frame-perfect re-jumps (trivial for a script) and `sv_enablebunnyhopping 0` clamps
speed at takeoff — but air-strafe acceleration AFTER the clamp is shared physics, and his bot
pumped back ~100 u/s per hop (clamp-capped launch ~300, landing ~400, hop after hop). The
owner had told players and admins bhop was closed; it is — for humans.

`movement.airgain` is the stack's first movement axis (LogicBreach): horizontal speed gained
WHILE AIRBORNE, median across CHAINED jump arcs. An arc must be shaped like a jump — upward
launch, 0.3–1.2 s airborne, ≤120 u z-span, re-launch within ~0.2 s of landing at speed — so a
surf ride (one long airborne phase; ramps never ground you) and a walked-off ledge are
structurally invisible, and a lone HE-boost can't move a median. Corpus baseline, 43 demos /
261 honest sessions with ≥4 chained arcs: median gain max +14.3 u/s (downhill bursts live
there — stamina kills them by hop three). C9: +67.3, 8/8 arcs ≥ +60.

Whisper (fusion) at median ≥ +25 over ≥4 arcs. The auto-action edge `airgain-chain` demands
median ≥ +40 (≈3× the honest maximum ever measured) AND median peak ≥ 300 u/s (over-sprint)
over ≥5 chained arcs — and its response is new: the pawn FREEZES IN PLACE for
`AirGainFreezeSeconds` (default 10 s; MOVETYPE_NONE stops gravity too, so a mid-chain catch
hangs the cheater in the air), then thaws. The freeze needs no `AutoActionEdges` entry, is
gated by `AutoActionEnabled` like every auto-action, and is fully audited (action log line,
admin evidence pair, public announce template `AirGainFreezeAnnounce`). The edge bypasses the
whisper cooldown — a whisper two hops earlier must not delay the freeze on a live script.

Config schema v23 → v24 (new keys: EnableAirGain, AirGainMinArcs, AirGainSignalMedianGain,
AirGainEdgeMinArcs, AirGainEdgeMedianGain, AirGainEdgeMinPeakSpeed, AirGainFreezeSeconds,
AirGainFreezeAnnounce — all defaulted, existing values untouched). 118 tests.

## v0.9.99 — admin-chat deliveries are logged (what was sent, and to whom)

C9's live Watch alert proved the pipeline end-to-end (signal → fusion → red admin notice →
human kick-ban 10 s later) — and exposed a blind spot while reconstructing it: private
`PrintToChat` lines reach only the clients they're addressed to. They are not in the GOTV
demo (targeted usermessages never reach the broadcast), not in the server chat log, and the
plugin didn't record sending them. "Did any admin actually see it?" had no answer in any log.

Now every admin-chat delivery — both the throttled Watch/Review suspicion notice and the
unconditional auto-action evidence pair — is logged at the moment of sending: a `notify`
JSON-line (kind, subject, exact payload with colour codes stripped, recipient count, and
each recipient's name + SteamID) plus a console line. `admins: 0` with an empty list is the
record that matters most: the notice fired into an empty room, check the console log instead.
Alert records also gain `wallClock` and `map` (signals always had them; alerts had to be
dated by their neighbouring lines during the C9 reconstruction).

Config schema unchanged (v23). 110 tests.

## v0.9.98 — overlay seed ships in the package (install + restart, nothing else)

The owner packages releases so every plugin file is replaced but the config is left alone —
so the one remaining manual step was creating `OSAntiCheat.local.json` in configs/ by hand.
Now the package can carry it: `release.sh` ships `private/OSAntiCheat.local.json` (gitignored)
inside the plugin folder when present, and on load the plugin copies it to
`configs/plugins/OSAntiCheat/` — ONLY if no local.json exists there yet. Copy-once: a
configs-side file always wins (it may be hand-edited), and later packages replacing the
plugin-folder seed never touch it again. A seeded zip is for the server, not for public
release pages — release.sh prints a loud note when the seed is included.

Config schema unchanged (v23). 110 tests.

## v0.9.97 — server-local config overlay (one deploy, one restart)

Every schema bump regenerates the config with defaults, wiping this server's pinned values —
so a release meant restart, re-edit BakesDir/GeoGate by hand, restart again. Two fixes:

- **`OSAntiCheat.local.json`** next to the generated config: holds ONLY the keys the server
  pins; applied on top of the parsed config at load. Nothing ever writes the file, so
  regeneration can't touch it. Unknown keys are skipped and logged loudly (a typo must not
  fail silently); `ConfigVersion` in the overlay is ignored; a malformed overlay logs an
  error and the plugin runs on the generated config alone. Applied keys are logged at load.
- **`WallhackGeoGate` now defaults to `true`** — the "off until live-validated" caveat is
  spent: validated across 27 maps with bake-on-load holding throughout (2026-08).

Config schema unchanged (v23). 110 tests.

## v0.9.96 — small-lobby gate on the information axes

Audit of a regular (2026-08-13, two 3-player matches) exposed a small-lobby artifact, cousin
to the night-map one: with one or two enemies, pre-aim knowledge is near-perfect and the
"rest of population" baseline is a couple of peers, so the information axes inflate for
EVERYONE present — live nulltest hit z=5–7 on all participants while DemoReplay's harder
axes stayed at zero, and the replay FAST/PRECOG sections flagged all three players
symmetrically. Log-wide, nulltest was 87% of all signals and had flagged 101 unique players
with near-identical profiles (top-15 median z ≈ 5) — population noise, not suspects.

- New `InfoAxesMinPlayers` (default 6): below this many players on teams (bots counted only
  with IncludeBots), wallhack.nulltest and aim.drift stop SAMPLING — not just emitting — so
  small-lobby evidence never pollutes the per-map totals a later, fuller lobby is judged
  against. Weapon axes are untouched: their physics doesn't change with lobby size.
- DemoReplay: the spotted->shot and aim-onset sections print a `[SMALL LOBBY]` caution when
  the demo has fewer than 6 human team players, so FAST/PRECOG lines read as context there.

Config version 23. 105 tests.

## v0.9.95 — admin chat throttle (notices must never drown the chat)

Owner: admins get spammed off the regular chat if every tier event pings. The engine re-raises
Watch every time a decaying score re-crosses the threshold (hover-spam), and a broadly-firing
axis can raise many players in one round. New `AdminChatThrottle`, presentation-layer only —
the JSONL/console log still records every alert:

- One Watch notice and one Review notice per player per map, ever. Re-crossings go to the log.
- Global quiet window between Watch notices (`AdminChatWatchQuietSeconds`, default 60; a
  suppressed notice is delivered on the next raise after the window, not lost). Review notices
  bypass the window; auto-action (kick) notices are never throttled.
- Resets on map change and slot vacancy.

Config version 22. 105 tests.

## v0.9.94 — aim.drift fusion axis + plain-language admin watch notices

The C8 behaviour hunt (aim-pattern battery over 23 demos / 305 honest sessions) found the
cross-archetype signature: the fraction of moving aim steps that REDUCE the error toward the
nearest enemy. Honest population: median 51.1%, absolute max 56.6%, per-lobby-z max 2.79.
C8 (soft aim): 59.6%, z=+4.40 — above every honest session, binomial z=+5.3 on 977 steps from
ONE minute alive. C3 (multihack): 56.0%, z=+4.03. C5 (silent aim): 50.1% — structurally
invisible to aim axes, stays owned by aimbot.silent. Retarget time corroborates the C8 type
(0.125 s median target switches vs honest corpus minimum 0.14 s / p5 0.33 s) but the margin is
one tick — profile colour, not a gate.

- New `AimDriftDetector` (`aim.drift`, Behavioural, weight 0.5, NO edge — can never act alone):
  tick-exact steps reconstructed from the ring buffers at the 20 Hz poll; votes gated on
  `AimDriftMinSteps` (500 ≈ 30–60 s of engaged play — the step is the evidence unit, kills are
  irrelevant), per-lobby two-proportion z ≥ `AimDriftMinZ` (3.0, above the honest max 2.79),
  abstention below `AimDriftMinPopSteps` (3000) of lobby baseline, and one emission per integer
  z band. Fluke tolerance is the design: a borderline session whispers one decaying signal;
  reaching the Watch notice takes sustained drift or a second axis corroborating.
- **Admin watch notices are now plain language** (owner: "admins don't get the numbers"):
  `keep an eye on <name>: aim pulls toward enemies unusually often — could be luck, not proof`
  instead of detector ids + scores. Numbers stay in the JSONL log for calibration. Auto-action
  notices (CHEAT KICKED + steamid + evidence) are unchanged — kicks stay blunt.

Config version 21. 102 tests.

## v0.9.93 — kill-burst early-warning tier (signal at 2, kick still at 4)

Owner direction: identify the player as early as the data honestly allows. Measured per-KILL
first (and rejected): a "tracked a moving unseen target tightly" rule (victim moved ≥100u in
the run-up, mean aim error ≤3°) matches **796 honest kills** — sound-tracking is a skill — and
**zero** of C5/C6's kills, whose silent aim never pointed the view at the victim at all. The way
one kill happens does not separate; repetition on distinct victims does. What the distribution
does allow:

- 1 blind HS: routine (7,627 in the archive) — stays silent.
- **2 distinct blind HS in-window: early-warning signal** (edge-less, confidence 0.4) — 1.8% of
  honest sessions ever reach this (120/6,664), so it is suspicion for the fusion engine and
  admin awareness, never an action.
- **3: second warning** (confidence 0.6) — the measured honest maximum.
- **4: the `blind-hs-burst` edge fires** (unchanged) → kick.

No new config. 99 tests.

## v0.9.92 — blind-headshot-burst detection with auto-kick (blind-hs-burst edge)

C8 (2026-08-07 seabase, video-confirmed wall+aim on a fresh account): an ace of **six scout
headshots in 12.1 s**, running from spawn zoomed, tracking through walls and smoke — five of six
victims never once in his spotted mask (`sinceAttSawSec=-1`), sub-degree head error the tick
*before* each shot. Every hard axis missed it structurally: deadaim wants a PARKED crosshair (he
moved, 5–45°/s), snap wants a STEP onto the head (he was already sub-degree through the wall),
track wants LATERAL bearing-following (an intercept course toward the victim has ~none), revisit
wants a clutch park. The only axis that spoke was the live null test (z=11 seconds after the
ace). The miss defines the fix — the conjunction that survives is kill-anchored:

- New `KillBurstDetector` (`wallhack.killburst`, LogicBreach, weight 1.6): a HEADSHOT kill on an
  enemy the killer has **never once seen this map** counts toward a rolling window;
  ≥`KillBurstMinKills` (4) DISTINCT such victims inside `KillBurstWindowSeconds` (15) → signal
  carrying the new **`blind-hs-burst`** edge, in `AutoActionEdges` by default → kick. "Never
  seen" is whole-map spotted-mask memory fed at the 20 Hz wallhack poll (same cadence and
  semantics as the offline validation); resets on map change and slot vacancy. A sighted victim
  mid-burst neither counts nor breaks it (C8's fifth kill was sighted; the ace still fires on
  kill 4, at +4.8 s — before kills 5 and 6 ever happen).
- **Validation (archive6, 321,423 kills / 6,664 attacker-sessions with any blind HS):** bursts
  of ≥4 occur exactly TWICE — C5 (spin-silent) and C6 (psilent), both confirmed cheaters — and
  never for an honest player. The honest tail ends at exactly 3, all pistol-round openings
  (round 1: sight history still empty for everyone) — hence the floor at 4, do not lower it.
  C8's ace scores 5.
- Enforcement is kick-not-ban and the demo records regardless: every signal carries tick + map +
  wall-clock, so the post-hoc review path (find demo → `demo_gototick` → judge) is unchanged.

Config version 20. 99 tests.

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
