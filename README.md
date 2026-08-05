# OSAntiCheat

![status: experimental](https://img.shields.io/badge/status-experimental-orange)
![phase: calibration & data-gen](https://img.shields.io/badge/phase-calibration%20%26%20data--gen-blue)
![version](https://img.shields.io/badge/version-0.9.6-informational)
![response: log--only](https://img.shields.io/badge/response-log--only-green)
![license: MIT](https://img.shields.io/badge/license-MIT-lightgrey)

> [!IMPORTANT]
> **Early-stage research project — not production-ready.** OSAntiCheat is under active
> development. Right now we are in a **calibration & data-generation phase**: detectors are being
> tuned and validated against real server data, thresholds are *not* final, and detection axes
> are still being added and reworked. Expect breaking changes between versions.
>
> Because it is statistical, it flags **probabilities, not proof**. The current response is
> **log + admin notice only — it never auto-kicks or bans.** Do **not** deploy it as your sole
> line of defense, and do not act punitively on its output yet. Follow along and star the repo
> if you want to watch it mature. ⭐

Server-side, heuristic anticheat for **CS2**, built as a
[CounterStrikeSharp](https://docs.cssharp.dev/) plugin in C# (.NET 10).

> **Server-side only — no client component.** We observe only what the server sees
> (positions, view angles, shots, timing) and infer cheating statistically. That means we
> flag *probabilities*, not proof — but it's impossible for a cheater to bypass by hiding
> their client. The v1 response is **log + admin notice only**, never auto kick/ban.

## Method — measure first, gate on impossibility, corroborate

Three rules shaped everything that works here, all learned the hard way:

1. **Measure the honest population before trusting a threshold.** Every gate below was *read off*
   real data (a public 17k-demo archive of the server's own matches, replayed through the exact
   detector code), never imported or guessed. Our first wallhack heuristic flagged ~100 % of
   regular players; the first fusion build alerted on the entire server base. The population
   baseline is the product — the detector is just a comparison against it.
2. **Hard edges sit on true-zero baselines.** A LOGIC-BREACH detector only ships if the honest
   population *never* produces the signature — not rarely, never. Examples from the measured
   archive: no human ever went ≥5° off a head to exactly on it in one tick (0 in 318k kills); no
   registered first-of-burst bullet hit ever had the shooter's view ≥10° off the victim (0 in
   3,486); no one ever reversed a ≥45° aim direction more than once consecutively.
3. **No single signal condemns — with one carefully-earned exception.** Everything feeds a
   **fusion engine** (graded confidence, exponential decay, corroboration bonus across
   *independent* axes, `Watch`/`Review` tiers). Unvalidated axes run in **shadow mode**: they
   log everything for calibration but cannot fuse or alert. A detection is a *suspect for human
   review*, never a verdict. The exception (v0.9.8): two signatures that are **physically
   impossible** for a real client *and* measured-zero across the whole archive carry a
   deterministic *edge* and may trigger an automatic response (default: kick) — see
   [Enforcement](#enforcement) below.

The axes split into two families — **mechanics** (the hand does something impossible) and
**information** (the player knows something they cannot know). Small samples still convict when
each event is individually near-impossible: two banned players were typed at *P* ≈ 2×10⁻¹⁷ and
1.5×10⁻¹² from just 6–8 kills each, by placing every kill in the population distribution and
taking binomial tails.

## Detectors

**Live as of [v0.9.8](https://github.com/Pintuzoft/OSAntiCheat/releases):** all six Tier-1 axes
below run on the server; the Tier-2 axes run in shadow. Every Tier-1 gate is placed
where the measured honest population has **zero** events — so any signal is, by construction,
something the population has never produced. Two Tier-1 signatures additionally carry an
auto-action edge (see [Enforcement](#enforcement)); everything else is log + admin notice.

**Tier 1 — logic breach** (mechanical impossibility, measured true-zero honest baselines):

| Detector | Signal | Honest baseline (measured) |
|---|---|---|
| **Spinbot** | Sustained >360° continuous rotation with a headshot kill mid-spin | Human flicks are 200–300° and *stop on the target* |
| **Bone-lock** | Repeated shots landing ≤0.05° from the head *centre* — below angle-quantization | Humans land on a 1–2° motor hump, never sub-quant |
| **Snap** | ≥5° off a head one tick before the shot → ≤0.05° on its centre at the shot | 0 of 318k kills; honest tails end at 4.53° approach / 0.153° landing |
| **Silent aim** | A bullet *registers damage* while the view points ≥10° away from every position the victim held in the lag-comp window | Honest burst-opener max: 8.0° (0 of 3,486 ≥10°). Catches both spin-silent and frozen-view psilent |
| **Anti-recoil** | Recoil compensation too consistent to be human | Human floor ratio ~0.06 across 17k archive sprays |
| **Anti-aim** | Pitch past the engine's ±89° clamp, or ≥6 consecutive sign-alternating ≥45° yaw jerks | Honest pitch parks at exactly 89.00; honest max alternation: 1 |

**Tier 2 — behavioural / information** (improbable, human-reviewed):

| Detector | Signal | Status |
|---|---|---|
| **Null test** ⭐ | Crosshair on an **unspotted** enemy's *present* position more often than its *1.5s-past* position (McNemar z — skill cancels, playtime doesn't confound) | Shadowed live. Since v0.9.5 the absolute z is additionally gated on a **per-map population baseline** (two-proportion z vs everyone else on the map): live data showed night-variant maps inflate the whole population's z (unreliable spotted state), so only an excess *over your peers* survives. v0.9.7 recalibrates from the first live-caught labelled cheater + 5 days of shadow data: evidence gate 30 → 400 discordant samples (the 20 Hz polls are autocorrelated — small-n z is noise) and fusion weight 0.5 (skilled players have a genuine positive present-bias; the axis corroborates, never convicts). Replaying the fusion over the live window: Review sessions 97 → **1 — the cheater** |
| **Wallhack (track/gaze)** | Aim follows an unspotted enemy **provably occluded by static geometry** (BVH8 map bake raycast) and unseen by the observer's whole team; a silent enemy (no footsteps) boosts confidence | Shadowed, geo-gated live since v0.9.4 across every baked map. v0.9.5 adds a measured min-enemy-movement gate: a stationary enemy's bearing sweep is generated by the *observer's own* motion and is not tracking evidence |
| **Aim sweep / triggerbot** | Mid-sweep on-target shots / instant fire on crossing | Shadowed: falsified as-designed against the archive; the data collection informs their replacements |

Shadow mode is deliberate: a muted detector gathers no data, a shadowed one gathers everything
without polluting the score — ammunition for a future model over the whole feature vector.

## Enforcement

As of v0.9.8 the plugin can act on its own — but only on signals carrying a **deterministic
edge**, a signature that is physically impossible for a real client *and* has a measured-zero
honest baseline:

- **`spin-hs-kill`** — a headshot kill lands while >360° of *continuous* same-direction rotation
  is still whirling ≥1200°/s at the kill tick, twice (`SpinbotMinSpinHsKills`). A legit
  trickshot-360 stops to aim, which breaks the spin before the shot; 0 events in 321k archive
  kills.
- **`fake-pitch`** — view pitch past the engine's server-side ±89° clamp for 3+ consecutive
  ticks. The honest population parks at exactly 89.00 and cannot exceed it; only input that
  bypassed the normal client path can.

The default response is **kick + public announce** (`AutoActionCommand` / `AutoActionAnnounce`,
placeholders `{slot} {userid} {steamid} {name}`) — a kick costs a wrong player a reconnect, a
wrong ban a player, so escalation to a ban system is a config choice
(`"css_ban {steamid} 0 cheating"`), not a default. `AutoActionEnabled=false` reverts to dry-run:
the breach is still logged with the exact command it *would* have run. Every action decision —
executed or dry-run — is written to the JSON-lines log as a `type:"action"` row next to the
signal that caused it. Bots are never acted on, and no probabilistic axis can reach this path:
the edge whitelist (`AutoActionEdges`) is checked against a field only the two detectors above
ever set. The poll-based continuous-spin and yaw-jitter signals deliberately carry **no** edge —
they fuse toward human review like everything else.

## History & field results

The full per-version story — calibration numbers, the first live deployment, the banned-player
forensics that convicted two cheaters at 10⁻¹²–10⁻¹⁷ and became the silent-aim detector — lives
in **[CHANGES.md](CHANGES.md)** (one entry per release) and the lab log **[TODO.md](TODO.md)**
(pseudonymized; the roadmap, every measurement, and every honest dead end).

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

## Acknowledgments

OSAntiCheat is server-side and statistical, but several detection *concepts* trace back to
earlier open-source SourceMod anti-cheats. These projects are largely abandoned now; we build on
their ideas and credit them where due:

- **SMAC** — SourceMod Anti-Cheat — aim-snap-on-kill, spin yaw-velocity gating (with a
  sensitivity exclusion to spare legit high-sens flicks).
- **Little Anti-Cheat / Lilac** — impossible pitch/roll angle bounds, ConVar-query cheats,
  NoLerp interp math.
- **CoW Anti-Cheat** — event-timing checks (e.g. instant-defuse).

Those are Source 1 and built on the per-command usercmd stream. OSAntiCheat reimplements the
applicable ideas in C# from per-tick server state for CS2 / Source 2 — **concepts only, no code
is copied.** Where a specific detector adapts an idea, it is cited inline at the detector.

📖 **[docs/prior-art.md](docs/prior-art.md)** is our open review log of every anti-cheat (and
academic project) we studied — what each detected, whether it's reproducible in a server-side
statistical model, and an honest verdict on what we could take from it.

## License

MIT — see [LICENSE](LICENSE).
