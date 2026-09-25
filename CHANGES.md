# Changes

Version history for OSAntiCheat, newest first. Every release gets an entry here; the README
describes the current state only. Player/admin names follow the pseudonym scheme from
[TODO.md](TODO.md) (Cn = typed cheater, Gn = griefer, Rn = regular, An = admin).

## v0.9.114 — a glimpse at the trigger is still a glimpse (the first stale-grade alert was a regular)

Four quiet evenings after v0.9.113 (2026-09-21→24: 758 log rows, ~50 players, aim.drift at zero
alerts for 32 days now) carried one alert, and it was the first thing the v0.9.112 stale grade
ever raised: R18, a regular with fifty play-days in the log, three deagle headshots in 10.4 s on
de_foroglio in a 3v3, Watch at 1.04, "several quick headshots on players they never saw". No
admin was online to read it. The demo says otherwise on every axis (no tier, head error median
1.7°, recoil ratio 0.79, 23% hit rate) and the burst itself comes apart on the kill rows: two
victims were last seen in the pistol round (32.8 s and 45.8 s earlier — stale by the rule, and
correctly so), and the third died at 79 units in a corner meeting, spotted by the killer two
ticks (31 ms) before the shot. The archive instrument every killburst rate was read from — the
mask as it stands at the kill — calls that victim seen, so the burst is two stale victims, below
the three-victim grade, and nothing fires. Live called him blind, because the plugin's sight
memory had only one feed, the 20 Hz poll, and a sighting that opens under 50 ms before the kill
never reaches it. The plugin was stricter than its own calibration.

Lobby size was the tempting explanation and it is not one: the archive's stale-grade triples
sit in the big lobbies (0 of 3,919 attacker-sessions with 2–6 players, 18 of 30,647 with 9+),
and the sixteen honest ones are the server's best regulars — a stammis-ace signature, which is
what the 0.04% tier was accepted as. No lobby gate.

- **Sight at the kill tick counts as sight.** The death handler reads the victim's spotted
  mask for the killer before asking killburst, and notes the sighting if the bit is set — the
  same read the offline kill rows make. Two ticks of sight is sight.
- The mask bit maths moves into one helper (`SpottedMask.IsSetFor`) shared by the poll, the
  kill handler and `css_osac_los`, so the three cannot drift apart.
- The C11 open question from v0.9.113 (offline never-seen, live silent) is the same instrument
  gap in the other direction and stays open; this release only makes live at least as lenient
  as the archive at the kill itself.

Three new tests: the R18 sequence (two stale victims around a kill-tick sighting stays silent)
and the mask helper's two words. No config change (schema v31). 143 tests.

## v0.9.113 — the bullet went where the eyes did not (the eleventh typed cheater, in 83 seconds)

A fresh account, two and a half minutes on the server across two maps (2026-09-20): one deagle
headshot on the tail of de_island, then the pistol round on de_canals — four deagle headshots at
789–1203 units inside 21 s, admin-kicked 83 s into the map. Chat before the round: "I will not die."
The plugin raised nothing, the offline replay nothing, the geometry sweep nothing. The owner's read
("aims two metres beside and lands the headshot") was exactly right, and it was silent aim, not snap:
at every kill the replicated view sat 2–6° off the head with no step at the shot, and twice the
crosshair was CLOSER to another enemy than to the one whose head the bullet found (Stern: view 5.8°
off, AizZ 4.0° away behind a wall; ArMoKS: view 5.7° off, Skugan 1.9° away). All of it under the
10° floor the silent-aim detector had.

That floor was read from the all-weapon hurts tail — and rifles own that tail. Honest far (≥700u)
headshot kills with the view >4° off the head: ak47 8.4%, m4a1 9.1%, mp9 15% (spray, recoil comp).
A precision weapon at range has no such tail: deagle 0.07% (n=5,362; p99 1.94°, p99.9 3.55°, max
5.07°), usp 0.06%, glock 0.14%, awp 0 of 1,184, scout 0.12%. Per attacker-session, TWO or more such
hits: 0 honest of 42,342 — the single archive session at ≥2 is C5, the spin-silent cheater (3 of 5).
The measure on this branch is the view-to-HEAD error at the kill tick; the detector's own
(minimum over lag-comp candidates of the view-to-nearest-body error, head sample included) is
never larger, so the honest rate under it can only be lower.

- **aimbot.silent gains a far-precision grade.** A HEAD hit (player_hurt hitgroup 1) with a
  precision weapon — deagle, R8, USP/P2000, glock, AWP, scout, autosnipers — at ≥700u counts from
  `SilentAimFarOffDeg` (4°) instead of the flat 10°. One such hit is a 0.6 whisper (doubles another
  axis, never a tier alone; 6 honest sessions of 42,342 hold one). A second DISTINCT victim on the
  same map is the `silent-far-hs` edge, armed by default in `AutoActionEdges`
  (`SilentAimFarMinVictims` = 2). C11 would have been kicked at the Stern kill, 37 s before the
  admin did it by hand. Spray pistols and every rifle/SMG stay on the flat grade.
- The far grade's victim memory is per map (`Reset` on map change), like the archive sessions it
  was read from; the flat grade's rolling windows reset with it.
- DemoReplay feeds the hurt's hitgroup, so demos replay the new grade. Replayed over every
  local demo — 173 player-sessions in 12 maps, the C7–C10 lobbies and five plain evenings among
  them — the grade speaks for exactly three sessions: C11 on canals (whisper at the TacoTony kill,
  edge at Stern), C11 on island (one whisper), and C9 on boston 2026-08-14 (deagle headshots 5.6°
  and 7.2° off at 841–856u, a scout headshot 12.6° off at 1,484u: whisper, then the edge 46 s
  later — a second, independent edge on the cheater killburst kicked). Zero of the other 170.
  C7, C8 and C10 stay silent, as their humanised-aim archetype should.
- Open question from the same demo: the offline kill rows say Stern and AizZ were both never in
  the killer's spotted mask (6 s apart), which is killburst's two-victim early warning — and the
  live plugin said nothing. Hypothesis: the mask flipped during the ~0.4 s each victim was exposed
  before the shot (the 20 Hz poll sees it; the demo's networked mask lags). Unverified; a
  "seen-at-kill" debug line is the next step if it recurs.

Six new tests: the whisper, the pair, the same-victim repeat, the gates (body hit, rifle, spray
pistol, near range, view on the head), the lag-compensated head hit, the map reset. Config schema
v30 → v31 (two keys added, one edge armed). 140 tests.

## v0.9.112 — a minute-old glimpse is not sight (the tenth typed cheater walked through)

The same log that carried the mouse swipe carried a real one: a fresh account, two maps,
seventeen kills and two deaths in 5.6 alive-minutes, admin-kicked after eleven minutes
(2026-09-18, the owner's label: wall + aim). The plugin raised nothing. What it held: aim.drift
climbing z 3.8→6.1 in 96 s (corroborate-only since v0.9.108), the null test at z=11 within 144 s
of the second map (shadow, and 75% present-rate is rank 78 of 2,497 live sessions — regulars get
there too), and ONE blind headshot from killburst in a round-3 spree of three in 8.5 s: the other
two victims had been in the killer's spotted mask 64 s and 147 s earlier, so the strict "never
seen this map" rule let them explain a headshot they could not have placed.

Both demos replayed offline: no signal on any axis (head error median 1.5–1.9°, no lock, no snap,
silent max 3.7°, null-test ratio 1.10). The crosshair told nothing either: blind-gaze share on
team-unspotted enemies at every cone from 5° to 25° sits mid-lobby, follow episodes on unseen
movers 1.8–3.2/min against a lobby of 1.4–4.7, and the parameter sweep ranks him first in both
matches only by margins a 2.8-minute sample cannot carry. The owner's spectator recordings agree:
the crosshair is not glued to silhouettes behind walls; the information is used to be in the
right place and to fire the instant a target appears. This archetype (C2, C8, now C10) leaves
one measurable trace — lethality: 57% and 53% of shots hit at 2.5 and 3.6 kills per alive-minute.
Among 40,119 archive sessions with ≥40 shots and ≥2.5 alive-minutes, ZERO combine ≥50% with
≥2.0 (the 99.9th percentiles are 0.53 and 2.51 separately). Noted as the next axis; not built.

- **Sightings go stale.** `KillBurstBlindAfterSeconds` (default 30): a victim the attacker has
  not had spotted for that long is blind again. Measured on 42,342 archive attacker-sessions:
  ≥3 distinct victims under the stale grade in one 15 s window = 19 sessions (2 typed cheaters,
  0.04% honest), ≥4 = 3 sessions, all three typed cheaters — the edge keeps its floor of four
  under the looser definition and gains one archive cheater. Across the five labelled demos
  (C7–C10) the stale grade moves C10 from 1 to 3 and nobody else: C7 1, C8 5, C9 2, honest
  maximum 2. C10's round 3 would have read "3 headshot kills in 8.5 s on 3 different enemies
  the killer had not seen for 30 s or ever".
- **The three-victim tier now carries a Watch on its own** (0.65 × weight 1.6 = 1.04): a lead
  this rare (one honest session in ~2,500) is worth the yellow "keep an eye on … could be luck"
  line. The two-victim early warning keeps the strict never-seen grade at 0.4: under the stale
  grade it would fire in 1.7% of sessions, six times its measured rate, for a whisper whose only
  job is to double another axis.
- Sweep gains `--trace-dump <tsv>`: every parsed per-poll observation (nearest unspotted enemy,
  aim error, team-unspotted flag, speed, positions), so gaze and dwell can be measured outside the
  detector's own gates. That is how the cone-by-cone table above was produced.

Two new tests: the C10 spree (two stale victims are no warning, the third makes a rare one) and
the peek kill (a victim sighted at the kill tick neither counts nor breaks, as before). Config
schema v29 → v30 (one key added). 134 tests.

## v0.9.111 — a burst is not a spin (the first poll-spin signal was the owner's mouse)

Forty-eight live days without a single spinbot signal, then nine in 1.6 s — on the owner's own
account (2026-09-10), trying a freshly installed wireless-mouse driver: one swipe of 752° in one
direction at spin rate, then still. Fusion made a Watch of the first poll and a Review of the
second, 0.2 s later, and the online admins got the red "impossible spinning" line. No action was
taken — the poll-spin carries no edge, and the kick edge (a headshot kill landed mid-spin, twice)
stayed silent, as designed. Two things were wrong, neither of them the 752°:

- **The poll re-read the same stretch nine times.** Every 0.2 s it re-measured the longest run in
  the 2 s ring buffer, found the same 752° and signalled again; fusion summed the first two into
  a Review. The rule airgain got in v0.9.107 and bonelock in v0.9.109 now applies here too:
  ticks credited to a stretch are spent, and the next signal must be rotation newer than its
  last tick.
- **Two continuous turns are not beyond a hand.** A high-sensitivity swipe can carry 2.1 turns
  without a lift; what a hand cannot do is keep going. The poll now credits the first two-turn
  stretch silently and fires on the NEXT one arriving within 2 s (a bot at the 1000°/s floor
  serves two turns every 0.72 s; the buffer spans 2 s) — the "once is a fluke, twice is a bot"
  rule the spin-HS-kill edge has had since v0.9.8. A spinbot at 2000°/s now alerts 0.8 s after it
  starts instead of 0.4 s; a single swipe, or two swipes seconds apart, never alert.

Replaying the log: the nine signals collapse to zero. The spinbot test now models a spin that
keeps going (poll 2 silent, poll 4 fires, then every fresh two turns); two new tests pin the
one-swipe burst and the two-bursts-seconds-apart shape to zero signals. No config change
(schema stays v29). 132 tests.

Noted, not code: the same log shows movement.airgain whispers at median peaks 252–278 carrying
v0.9.110's confidence span — the build is live, but the generated server config still holds
the v28 peak floor (250). A schema bump does not reliably regenerate the file; the 290 floor has
to be set on the server by hand.

## v0.9.110 — the whisper stays a whisper (three Watches on one regular)

movement.airgain's whisper carried three Watch alerts on the same regular (2026-08-29 ×2,
09-04) and sub-Watch signals on four more regulars in the same fortnight — seven signals, five
known regulars, no cheater. Every one passed both v0.9.107 gates correctly: 5–8 chained hops,
median gain +25…+39, median peak 258–280. Two things were wrong, neither of them the arcs:

- **The peak floor sat inside the human band.** 250 was chosen as the sprint cap ("a script
  bhops to go faster than running"). True below sprint — the R1's slow-hop FP peaked at 216 —
  but a hand that launches FROM a sprint air-strafes above it too, to 258–280. The script band
  starts at 300 (C9: 300–400 at +71 median). `AirGainSignalMinPeakSpeed` 250 → **290**: above
  every human chain measured, under the one auto-bunnyhop-only run on record (298 — perfect
  re-jump timing, human strafing).
- **The whisper could carry a Watch alone.** Confidence spanned 0.5–0.8 over gains +25…+40;
  times the axis's 1.5 fusion weight, every chain past +33 median scored ≥1.0 = Watch with
  nobody else speaking. The 09-04 alert was 1.08 from airgain plus 0.02 of decayed drift — no
  doubling involved. The span is now **0.40–0.60** (≤0.90 fused): a whisper doubles a case
  another axis opened, it never builds one — the rule drift got in v0.9.108, applied to the
  detector that was being called a whisper. The edge (0.95 → 1.43 fused) still alerts red by
  itself.

Replaying the log's airgain signals through the new gates: the seven regular chains are silent
and the only survivor is the owner's own auto-bunnyhop test (+31 at 298 → 0.48, 0.72 fused, no
tier). Both alerts airgain has carried alone since v0.9.107 (08-29, 09-04) vanish, nothing new
appears. C9's shape (+80 at 380, 3–4-hop bursts) still fires the edge in both edge tests, untouched.

Two new tests model the live band (a +36/260 chain from a 224 u/s launch stays silent) and pin
the carry rule (the strongest possible whisper, +39 at 301, fuses under Watch); the over-sprint
whisper test now uses the auto-bunnyhop shape. Reason strings drop "honest corpus max +21" for
the live band. Noted, not changed: the edge's gain gate (+40) now sits only just above the best
live hand (+39) — its beyond-human margin is the conjunction with the 300 u/s peak gate (hands:
280). It stays latent (dry-run) as before.

Config schema v28 → v29 (one default changed). 130 tests.

## v0.9.109 — one hold is one lock (first bonelock Review was a wallbang)

aimbot.bonelock's first live Review (2026-08-20 de_canals, a regular, "5 head-centre locks ≤0.050°,
latest 0.000°") replayed from the demo: six AK taps in 1.5 s at 300–450 ms spacing, view rate 0,
shooter stationary, enemy stationary behind a penetrable surface — damage 9/9/miss/41/33/10 and a
kill, never a headshot. The crosshair sat by chance within a quant step of the model head
(feet+64) and the detector counted the same frozen hold once per tap. The rest of his profile
is an ordinary hand: median head error 2.42° (worse than most of the lobby), spray spread 15.7°,
wall axes flat. Same lesson as the airgain whisper in v0.9.107: repetition is not evidence.

- **A lock counts only when the aim has RE-ACQUIRED it**: the view must have travelled
  ≥ `BoneLockReacquireDeg` (default 2°) from the last counted lock — any buffered tick or shot
  since then. A bot re-locks across engagements and travels between them; a held crosshair
  tapped six times is one lock. Schema v28.
- DemoReplay's offline counter mirrors the rule and prints the held taps separately
  (`spike<=0.05: 1 (+4 same-hold taps)`). The 20 Aug demo: MrJozk 5 → 1, level with two other
  regulars in the same lobby; the LOGIC BREACH section is empty.
- The bot test now models an actual bot (lock → travel → lock); a new test pins the frozen-hold
  shape to zero signals. 128 tests green.

Open: the only other live bonelock Watch (2026-08-15 de_maginot) has no demo in hand, so it is
unverified either way.

## v0.9.108 — drift may double a case, never build one (twelve regulars whispered)

Six nights of v0.9.107 produced nine whispers; eight were aim.drift ALONE on eight different
regulars (two of them admins, reading their own notice), and the log holds twelve such solo-drift
Watches since the axis went live 10 Aug — every one a known regular, not one of them a cheater.
The axis's honest tail is population-wide: since 16 Aug it signalled on 55 different players, and
a regular climbing z bands 3→7 inside one map (0.4+0.5+0.6+0.7+0.8 × 0.5 = 1.5) is over Watch
with nobody else speaking. "Sustained" turned out not to be evidence on its own.

- **Fusion gains a corroborate-only bucket** (`SuspicionConfig.CorroborateOnly`). A listed
  detector's score never carries a tier: it sits dormant until some carrying axis has a live
  (decayed) score for the same player, and then counts for AT MOST as much as those axes earned
  themselves — it can double a case, never build one. `AimDriftCorroborateOnly` (default true)
  puts aim.drift there. Schema v27.
- Replaying the log's signals through the live fusion reproduces all 21 alerts since 10 Aug 1:1
  (same players, same scores). Under the new rule: 9. The twelve solo-drift whispers vanish,
  nothing new appears, C9's killburst+drift Watch (14 Aug) fires in the same second with the
  same score, and MrJozk's bonelock Watch→Review (20 Aug) is untouched — bonelock carries alone.
- The whisper chain for drift now reads as designed: the axis corroborates a wall/aim case that
  another detector opened, exactly as it did next to C8's track kills and C9's killburst.

## v0.9.107 — the whisper learns what a script is for (first verified airgain FP)

An R1 hit Review on movement.airgain alone (2026-08-15 de_vandal) and the demo proves him
human. Two gaps, both closed:

- **The whisper now demands over-sprint peaks** (`AirGainSignalMinPeakSpeed`, default 250).
  The R1's one real chain gained a median +37 u/s — but from ~175 u/s launches up to a median
  peak of 216, SLOWER than running. A script bhops to go faster than sprint (C9 peaked
  300–400); a chain that never reaches 250 is a hand losing speed to its own landings and
  strafing some back. Big relative gain at sub-sprint speed is now recognized as the human
  signature it is. (The edge already had its own peak gate at 300 — only the whisper was blind.)
- **A whisper needs FRESH evidence: ≥2 chained arcs since the last signal.** Only the edge
  cleared the window; every landing re-evaluates it, so the R1's single burst re-fired on two
  innocent lone jumps as each 20 s cooldown lapsed — three identical signals for one event,
  1.12 → 1.96 → 2.64 = Review, pure repetition. Two arcs because a burst is by definition two
  landings: a second real burst inside the window still whispers again, a trailing hop or a
  stale re-read never does.

Either fix alone silences the whole incident — verified by replaying the R1's demo through
the real detector (the same reconstruction reproduces the live chain 1:1: same 5 arcs, same
+37/216 medians; post-fix, zero signals). C9 stays caught by construction: the edge branch is
untouched (it never needed the whisper gates — its own peak floor is 300) and both edge tests
model C9's exact shape. A flags-based demo replay of C9's boston session is blind to his
chains — script-tight re-jumps never show FL_ONGROUND in the demo, the same z-inference
limit v0.9.101 documented — so that check runs on the unit models, not the demo. Reason
strings also correct "honest corpus max +14" → "+21" (the windowed statistic from v0.9.101 —
the whole point of that release was that the rolling window is the honest yardstick).

Config schema v25 → v26 (one new key, defaulted). 124 tests.

## v0.9.106 — the freeze goes latent until a real cheater validates it

Owner's call, and the ladder's own rule applied to our newest edge: regular folk must never
be the test. New `AirGainFreezeArmed` (default false): the airgain edge still fires, fuses,
alerts red and hands admins the evidence pair + SteamID, and the action log records the
DRY-RUN ("CHEAT CONFIRMED (dry-run, NOT frozen)") — but nobody actually freezes. When the
first REAL bhop script walks into the trap and the would-have-frozen record reads clean,
flip the flag in the overlay and the statue business opens. Exactly how the kick edges
climbed (they shipped dry-run too, armed in v0.9.8 after validation).

Config schema v24 → v25 (one new key, defaulted). 121 tests.

## v0.9.105 — the speeding ticket

The freeze announce gets the owner's voice: " [OSAC] SPEEDING TICKET: {name} — impossible
mid-air acceleration (bunnyhop script). Parked as a statue until the round ends." Playful,
but still inside the messaging rule — "impossible acceleration" is literally the measurement
(honest max +21 u/s windowed median across 43 demos; the edge demands +40 at over-sprint).
If your config was already generated on v24 the old string is stored — regenerate or pin the
new one in the overlay.

Round-end auto-BAN was considered and deliberately NOT built yet: the edge has zero live
catches so far, and the ban ladder starts the same place the kick edges did — freeze +
evidence + human ban first, automation after the axis has a clean live record.

Config schema unchanged (v24). 121 tests.

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
