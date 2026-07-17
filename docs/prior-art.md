# Prior art — anti-cheats we reviewed

A running log of earlier anti-cheats (and academic work) we studied while designing OSAntiCheat,
with an honest verdict on whether we could take anything from each.

**The lens.** OSAntiCheat is server-side and statistical: it observes only per-tick server state
(positions, view angles, shots, timing) — no client component, no `usercmd` access. So for every
project the key question is: *does its detection rely on the client input stream (per-command
angles / buttons / mouse-deltas / `tickcount` / `cmdnum`), or on server-observable state we can
reproduce?* Anything usercmd-bound is a concept we can admire but not directly port.

We take **concepts only — no code is copied.** Most of these are GPL SourceMod plugins; we
reimplement applicable ideas in C# from scratch. Projects we actually build on are credited in
the README [Acknowledgments](../README.md#acknowledgments).

Legend: ✅ took / building on it · 🟡 candidate, needs a feasibility check · 🔎 concept confirmed,
validates an axis we already have · ⛔ nothing portable for us.

## SourceMod / plugin lineage (Source 1 — CS:S / CS:GO)

| Anti-cheat | What it detected | Server-side reproducible? | Verdict |
|---|---|---|---|
| **SMAC** (SourceMod Anti-Cheat; = KAC lineage, renamed) | Aim-snap-on-kill (>45° between samples), spin (yaw-vel >1440°/s, gated on `sensitivity ≤ 6`), eyetest (cmdnum/tickcount integrity), autotrigger, speedhack. Its "wallhack" = occlusion/culling, not detection. | Aim-snap & spin: yes (from sampled angles). Eyetest/autotrigger/speedhack: no (usercmd). | ✅ **Take** the spin **sensitivity-gate** exclusion; aim-snap-on-kill mirrors our aimbot(snap). Wallhack module is prevention, not detection. |
| **Little Anti-Cheat / Lilac** | Aimbot snap-% (post-shot within 10–20% of pre-shot distance), aimlock (<5° of target while own view moves >20°), impossible **pitch>89°/roll>50°** bounds, anti-duck-delay (`IN_BULLRUSH`), bhop, macro, backtrack, **ConVar queries** (11 graphics cvars), NoLerp math, high-ping. | ConVar queries / NoLerp / ping: **yes, pure server-side**. Angle bounds: yes. Aimbot/aimlock/bhop/macro/backtrack: no (usercmd). | ✅ **Take** impossible pitch/roll bounds, **ConVar-query cheats**, NoLerp math. 🟡 Aimlock is the "view-follows-enemy" idea — same false-positive trap our v0.3.1 data exposed; needs the null-test's past-control before it's usable. |
| **CoW Anti-Cheat** | Aimbot yaw-delta, bhop, silent-strafe, triggerbot, auto-shoot, perfect-strafe, AHK-strafe (mouse-delta patterns), macro, **instant-defuse**, cvar queries. | Only instant-defuse & event-timing. Everything else is usercmd / mouse-delta. | ⛔ **Nothing portable for us.** Its one server-side idea (instant-defuse) is a *false-positive generator on our server*, which runs an instant-defuse plugin by design. |
| **NoCheatZ-4** | *EyeAnglesTester* (impossible per-tick angle deltas), *ShotTester* (robotic fire-timing / triggerbot), *SpeedTester*, *JumpTester*. Wallhack/radar blockers = occlusion. | Eye-angle & shot-timing: yes. | 🔎 **Validates** our spinbot / eye-angle and triggerbot-timing axes. No new axis, but good confirmation the signals are sound. |
| **Oryx-AC** (GFL fork of shavitush/iNilo) | Explicitly **statistical/behavioral on movement**: distribution of strafe offset ("avg too close to 0"), "too many perfect strafes", scroll-interval analysis, scripted-jump histograms. | Yes (movement state). | ✅ **Take the methodology** — distribution-based flagging of *inhuman tightness* rather than single-input pattern-matching. Closest existing model to our statistical approach (targets movement, not aim, but the method transfers). |
| **StAC** (TF2, actively maintained) | **Fake eye-angles** (reported view angle vs actual shot angle mismatch → silent-aim), pSilent/angle-repeat aimbot, tickcount/backtrack, cmdnum manipulation, auto-bhop. | Fake-eye-angle mismatch: partially (needs shot-angle vs view-angle). Temporal checks: no. | 🟡 **Candidate:** the view-vs-shot-angle mismatch (silent-aim) is a signal we do **not** have yet. Worth a feasibility check on whether CS2 exposes both angles per shot. |
| **ReAimDetector** (CS 1.6, ReAPI/ReGameDLL) | Server-side aim-snap analysis; community replacement for ReGameDLL's weak built-in aim detector. | Likely yes (aim-snap). | 🟡 **Unverified** — couldn't confirm an authoritative repo with documented heuristics. Pending a targeted dig. |
| **HLGuard / AdminMod** (CS 1.6 / HLDS) | Server-side aim-snap + burstfire detection; wallhack = occlusion. | Aim-snap/burstfire: yes, but old and false-positive-prone. | 🔎 Marginal. Conceptual glance at burstfire timing only; low incremental value over NoCheatZ. |
| **ZBlock** (CS:S / 1.6) | Exploit & cvar **enforcement** (locking down exploitable configs), not behavioral inference. | N/A (config lockdown). | ⛔ Not a behavioral detector. Config-lockdown philosophy noted, nothing to port. |
| **Cheating-Death** (CS 1.6) | Client-side kernel driver + module-integrity (anti-tamper). | No — client-side. | ⛔ Not behavioral, not server-side, and was eventually bypassed. Skip. |
| **UAC** (milutinke/Ultimate-Anti-Cheat, CS 1.6) | WH-blocker + ReChecker signature-matching; states AIM/SHAKE, SPINBOT, RAPID targets. | No — closed-source binaries, no disclosed heuristics. | ⛔ Closed, nothing to learn. |
| **KAC** (Kigen's Anti-Cheat) | — | — | Same project as SMAC (renamed). Covered by SMAC row. |
| **ExAC** | — | — | ⛔ Could not verify it exists as a distinct AC; likely a confusion with SMAC/EAC. |

## The rare find — behavioral wallhack **detection**

| Technique | How it works | Reproducible? | Verdict |
|---|---|---|---|
| **Hallucination / phantom entity** (CS 1.6: "Lucia Hallucination", an "AimBot Detection" plugin; modern: [Activision research](https://www.activision.com/cdn/research/hallucinations), [arXiv 2409.14830](https://arxiv.org/pdf/2409.14830)) | Server injects a **fake enemy** where only a cheat can perceive it (inside a wall, in smoke, above the player). A wallhacker/aimbotter reacts — snaps aim, fires through geometry — and that reaction is 100% server-observable. A legit player can't see it and won't react. | **Yes** — fully server-side in our per-tick model. | ⭐ ✅ **Flagship candidate.** The **only behavioral wallhack *detection*** in the whole HL/Source/Source2 lineage (everyone else's "anti-wallhack" is occlusion/culling). *Active* detection — it creates observations instead of waiting for them, which directly attacks our small-sample problem. **Pending:** (1) can CounterStrikeSharp inject a hidden-but-server-tracked entity? (2) read the Activision source for authoritative mechanics (the CS 1.6 originals were partly reconstructed in research). Arms-race caveat: sophisticated cheats can filter phantoms. |

## Academic / ML (server-side CS cheat detection)

| Work | Approach | Verdict |
|---|---|---|
| **XGuardian** (USENIX Sec '26) | Wallhack detection from pitch/yaw alone + control, AUC ~0.97 with full trajectory shape. | ✅ **Already in use** — confirmed our view-angle-only approach works; see `TODO.md`. |
| **yviler/cs2-cheat-detection** | Two-layer LSTM on `.dem` ticks: pitch/yaw, velocity, **1st/2nd/3rd derivatives of aim angle**, cumulative displacement, per-kill 300×20 windows. Aimbot only. | ✅ **Take** the feature-engineering template. Closest public parallel to this project. |
| **"Aim Low, Shoot High"** ([arXiv 2004.12183](https://arxiv.org/abs/2004.12183)) | Red-team: humanized aimbots that evade angle-based detectors. | ✅ **Take as a red-team reference** — tells us which of our angle heuristics are brittle. |
| **YAACS** (arXiv 2607.04336), **Detecting Aimbot in MOGs** (arXiv 2606.07650), **AntiCheatPT** (arXiv 2508.06348) | DL/transformer aimbot detection on server-observable features (angular velocity/accel, shot timing, hit accuracy). | 🔎 Reference — feature/architecture ideas, nothing adopted yet. |

## Out of scope (why we didn't review them)

VAC / EAC / FACEIT / ESEA (closed, kernel/client anti-tamper), PunkBuster / XIGNCODE / GameGuard
(closed client), SourceBans++ (ban management, not detection), Overwatch & demo-analysis tools
(review/tooling, not plugins). **VACnet** (Valve's own server-side deep-learning aimbot detection,
GDC 2018) is closed but methodologically relevant — a pending conceptual read, tracked here so we
don't forget it.
