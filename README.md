# OSAntiCheat

Server-side, heuristic anticheat for **CS2**, built as a
[CounterStrikeSharp](https://docs.cssharp.dev/) plugin in C# (.NET 10).

> **Server-side only — no client component.** We observe only what the server sees
> (positions, view angles, shots, timing) and infer cheating statistically. That means we
> flag *probabilities*, not proof — but it's impossible for a cheater to bypass by hiding
> their client. The v1 response is **log + admin notice only**, never auto kick/ban.

## Status (v1)

Working detectors, verified by unit tests against synthetic tick data:

| Detector | Signal | Notes |
|---|---|---|
| **Spinbot** | Impossible sustained yaw rotation | Self-contained; lag-robust (skips sequence gaps) |
| **Aimbot (snap)** | Large pre-fire view snap that **lands on an enemy hurtbox** | Hitbox-agnostic (nearest body part, not the head); lag jumps land on nobody and are ignored |
| **Triggerbot** | Shot fired too fast after the crosshair **crosses onto** an enemy | Repetition-gated; ignores holds, sprays, bots, and enemies walking into a static aim |
| **Wallhack (track)** ⭐ | Aim **follows a moving unspotted enemy** through geometry — the crosshair tracks its bearing | Uses CS2's spotted system as the LOS signal. Requires the view to *follow* the movement, so a held angle an enemy crosses is not flagged (fixed after live-data false positives) |
| **Wallhack (gaze)** ⭐ | **Gaze follows** an unspotted enemy's movement — glancing/tracking, not a precise lock | View yaw must move *in step* with the enemy's bearing (not just point in its direction). Round-start window weighted higher (no legit info yet) |
| **Null test** ⭐ | Crosshair on an **unspotted** enemy's *present* position more often than on its *1.5s-past* position | Measures an information channel, not skill: game sense correlates aim with the past too, so the present-over-past asymmetry isolates present-knowledge-while-unseen = wallhack. Scored as a McNemar z (self-calibrating; skill cancels; no playtime confound) — see below |

All signals feed a **fusion engine** that triangulates independent axes into a per-player
suspicion score (graded confidence, exponential decay, corroboration bonus, `Watch`/`Review`
tiers). No single signal condemns — corroboration across detectors does, and a human reviews.

## Calibration (v0.5.0)

`wallhack.track`'s defaults come from a parameter sweep against real demos containing three
admin-banned cheaters, each alongside the legit players of their own match — not from guesswork.
Measured in signals per alive-minute (score accumulates, so raw score just measures playtime):

| | rate/min |
|---|---|
| **Banned cheaters** (3) | **0.68 – 1.23** |
| Highest of 133 legit sessions | 0.21 |
| Best players at their best (three top legit regulars) | 0.10 / 0.07 / 0.00 |
| Legit median | 0.00 |

Zero of 133 legit sessions reached the lowest cheater. Legit baseline is 0.026/min over 1132
minutes — cheaters run **30×** that (P(≥5 signals | legit baseline) ≈ 1e-6).

**Limits, honestly:** the config was *selected* on those three cheaters, so their numbers are
optimistic and the p-value doesn't price in searching 1600 configs. All three likely ran the
same cheat, so this shows the detector catches *that cheat*, not wallhacks in general. Only five
cheater signals in total. The legit baseline is the solid half. Replay tooling lives in
[tools/](tools/) — the next banned cheater is a held-out test.

### New in v0.6.0 — the null test as a live detector

Replaying 11 demos over the server's own ban list (verified cheaters + their matches' regulars)
found the tracking detector fires on the *regulars* (they are the ones scanning), while the **null
test** — present-position hits minus 1.5s-past-position hits on unspotted enemies — ranked the
verified cheaters **1st, 2nd and 8th of 70**. It is now a live detector (`wallhack.nulltest`).
### v0.6.1 — scored as a McNemar z-test

Live data exposed two flaws in the raw excess (present-rate − past-rate): it was noisy at low
sample counts (early +2–6pp readings were chance, converging to ~0.1–0.5pp), and its accumulated
score just re-measured playtime — the whole population drifted to Review on exposure alone. v0.6.1
replaces it with a **McNemar test**: of the samples where present and past *disagree*, it forms
`z = (b − c) / sqrt(b + c)` from present-hit-not-past (`b`) vs past-hit-not-present (`c`). Skill
cancels (concordant hits are ignored), it is self-calibrating (`z ≥ 3` ≈ 99.9% the asymmetry is
not chance, server-independent), and a player with no real effect stays at `z ≈ 0` however long
they play — so it no longer confounds playtime. Emission is escalation-gated to one signal per
z-band. Config: `NullTestMinObservations` (30) and `NullTestMinZ` (3.0). Still log-only.

See [TODO.md](TODO.md) for the full roadmap, including the wallhack / soft-aim / information-
causality detectors planned for later phases (which depend on server-side raycasting and
audibility modelling — to be verified first).

## Build & release

```bash
./scripts/build.sh               # build (Release) + run tests
./scripts/clean.sh               # remove bin/ obj/ dist/
./scripts/release.sh             # -> dist/OSAntiCheat_v<version>.zip
```

`release.sh` produces a zip containing a single `OSAntiCheat/` folder, ready to extract into
`.../counterstrikesharp/plugins/`. The version comes from `<Version>` in the csproj; the
CounterStrikeSharp API dll is host-provided and intentionally not shipped.

Configuration (thresholds, detector toggles, log path) is generated on first load — see
[OSAntiCheatConfig](src/Config/OSAntiCheatConfig.cs). The `css_osac_debug` in-game command
dumps your latest tracked sample to verify the sampler on a live server.

## License

MIT — see [LICENSE](LICENSE).
