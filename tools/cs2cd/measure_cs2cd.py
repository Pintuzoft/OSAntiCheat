#!/usr/bin/env python3
"""Measure aim metrics on the CS2CD dataset - the only labelled cheater data available for
aimbot/triggerbot, which otherwise has none at all.

    https://huggingface.co/datasets/CS2CD/CS2CD.Counter-Strike_2_Cheat_Detection
    317 matches with verified cheaters, 478 without.

Each match is a .parquet of per-tick player rows plus a .json whose "cheaters" key names the
labelled players. SteamIDs are pseudonyms (Player_1..Player_10), which is fine: every comparison
here is within a match.

What this CANNOT do: wallhack. The dataset's `spotted` is a plain bool ("seen by anyone"), not
the per-observer SpottedByMask the wallhack premise needs ("not seen BY YOU").

Caveats from the dataset's own README, worth carrying into any conclusion:
  * Only matches WITH a VAC-banned player were manually verified.
  * Inside those, the "not cheater" label is only 55.6% precise - nearly a coin flip. The
    cheater label is the trustworthy half.
  * "No cheater present" matches are unverified; cheaters may be in them.

Geometry mirrors src/Detection/Geometry.cs: Source engine angles, nearest hurtbox sample.

    python measure_cs2cd.py <dir> --out cs2cd_shots.csv [--jobs N]
    python measure_cs2cd.py --analyse cs2cd_shots.csv
"""
import argparse
import json
import os
import sys
from collections import defaultdict
from concurrent.futures import ProcessPoolExecutor, as_completed

import numpy as np

try:
    import pyarrow.parquet as pq
except ImportError:
    sys.exit("needs pyarrow:  pip install pyarrow numpy")

TICKRATE = 64.0
ON_TARGET_DEG = 5.0          # crosshair counts as "on" the enemy
# deg/s: the view is still travelling, not settled on a target. At 64 tick one sample is 15.6ms,
# so this is also a hard ceiling on how far the crosshair can have moved since the last one --
# 90 deg/s is 1.4 deg per tick. Demanding a large single-tick crossing on top of a rate gate is
# self-defeating: the first version paired this with "8 deg off one tick ago", which silently
# required 512 deg/s and matched nothing but wild flicks.
SWEEP_RATE = 90.0
BODY_HEIGHTS = (8.0, 46.0, 64.0)   # hurtbox sampled feet/chest/head, as in Geometry.cs
EYE_HEIGHT = 64.0

COLS = ["tick", "steamid", "X", "Y", "Z", "pitch", "yaw", "is_alive", "team_num", "shots_fired"]


def forward(pitch, yaw):
    """Unit view direction, Source AngleVectors."""
    p, y = np.radians(pitch), np.radians(yaw)
    cp = np.cos(p)
    return np.stack([cp * np.cos(y), cp * np.sin(y), -np.sin(p)], axis=-1)


def aim_error_deg(eye, fwd, feet):
    """Smallest angle from the view direction to any sampled point on a body. Never the head
    alone: a cheat can lock to any hitbox."""
    best = 180.0
    for h in BODY_HEIGHTS:
        d = np.array([feet[0] - eye[0], feet[1] - eye[1], feet[2] + h - eye[2]])
        n = np.linalg.norm(d)
        if n < 1e-6:
            return 0.0
        dot = float(np.clip(np.dot(fwd, d / n), -1.0, 1.0))
        best = min(best, np.degrees(np.arccos(dot)))
    return best


def angle_between(p1, y1, p2, y2):
    dot = float(np.clip(np.dot(forward(p1, y1), forward(p2, y2)), -1.0, 1.0))
    return np.degrees(np.arccos(dot))


def measure(parquet_path):
    base = os.path.splitext(parquet_path)[0]
    with open(base + ".json") as f:
        events = json.load(f)
    cheaters = {c["steamid"] for c in events.get("cheaters", [])}

    # Hits, straight from the event log. An aimbot has to hit more often - that is the entire
    # point of it - which makes accuracy a far more direct signal than how fast a crosshair
    # travelled. Headshot rate is included but is the weaker half: a cheat can be told to aim at
    # any hitbox, and plenty are set to the chest precisely to keep HS% ordinary.
    # player_hurt covers ALL damage, so counting it raw gives nonsense - the first test match had
    # a player at 181% "accuracy". Two things inflate it: a shotgun pull logs nine pellet hurts
    # against one shots_fired, and a bullet that penetrates one player into another logs two.
    #
    # Both have the same shape: one bullet is fired on exactly one tick, so several hurts from the
    # same attacker on the same tick are that one bullet. Dedupe on (attacker, tick) and shotguns
    # need no special case. Utility still has to go: it deals damage with no shot behind it.
    NOT_BULLETS = {"hegrenade", "inferno", "molotov", "incgrenade", "flashbang", "decoy",
                   "smokegrenade", "knife", "bayonet", "taser", "world", "fall"}
    seen_bullets, head_bullets = set(), set()
    for h in events.get("player_hurt", []):
        a = h.get("attacker_steamid")
        if not a or a == h.get("user_steamid"):
            continue
        w = (h.get("weapon") or "").lower()
        if any(k in w for k in NOT_BULLETS):
            continue
        key = (a, h.get("tick"))
        seen_bullets.add(key)
        if h.get("hitgroup") == "head":
            head_bullets.add(key)
    hits, heads = {}, {}
    for a, _ in seen_bullets:
        hits[a] = hits.get(a, 0) + 1
    for a, _ in head_bullets:
        heads[a] = heads.get(a, 0) + 1

    t = pq.ParquetFile(parquet_path).read(columns=COLS)
    d = {c: np.asarray(t.column(c).to_pylist()) for c in COLS}

    ticks = d["tick"].astype(np.int64)
    sids = d["steamid"]
    uniq_ticks, tick_idx = np.unique(ticks, return_inverse=True)
    uniq_players, player_idx = np.unique(sids, return_inverse=True)
    nt, npl = len(uniq_ticks), len(uniq_players)

    def grid(col, fill=np.nan, dtype=float):
        g = np.full((nt, npl), fill, dtype=dtype)
        g[tick_idx, player_idx] = np.nan_to_num(d[col].astype(float), nan=fill)
        return g

    X, Y, Z = grid("X"), grid("Y"), grid("Z")
    pitch, yaw = grid("pitch"), grid("yaw")
    alive = grid("is_alive", 0.0) > 0.5
    team = grid("team_num", 0.0)
    shots = grid("shots_fired", 0.0)

    # shots_fired is a cumulative counter: an increase means a shot was fired on that tick.
    fired = np.zeros_like(shots, dtype=bool)
    fired[1:] = shots[1:] > shots[:-1]

    rows = []
    # Per-player memory of the previous shot, for switch and dwell.
    prev_target = [None] * npl
    prev_time = [0.0] * npl
    prev_pitch = [0.0] * npl
    prev_yaw = [0.0] * npl
    prev_on = [False] * npl
    dwell_who = [None] * npl
    dwell_since = [-1.0] * npl

    shot_t, shot_p = np.nonzero(fired)
    for ti, pi in zip(shot_t, shot_p):
        if not alive[ti, pi] or team[ti, pi] not in (2.0, 3.0):
            continue
        now = uniq_ticks[ti] / TICKRATE
        eye = (X[ti, pi], Y[ti, pi], Z[ti, pi] + EYE_HEIGHT)
        fwd = forward(pitch[ti, pi], yaw[ti, pi])

        nearest, nearest_err = None, 1e9
        for oi in range(npl):
            if oi == pi or not alive[ti, oi]:
                continue
            if team[ti, oi] == team[ti, pi] or team[ti, oi] not in (2.0, 3.0):
                continue
            err = aim_error_deg(eye, fwd, (X[ti, oi], Y[ti, oi], Z[ti, oi]))
            if err < nearest_err:
                nearest, nearest_err = oi, err
        if nearest is None:
            continue

        on_target = nearest_err <= ON_TARGET_DEG

        # A switch counts only when BOTH shots were actually ON their targets. Without that the
        # "nearest enemy" just flips to whoever is least far away while you spray at nothing,
        # manufacturing 90-degree switches in 62 ms - the artefact that made a known-legit player
        # look superhuman on the live archive.
        switch_ms = switch_deg = -1.0
        if on_target and prev_on[pi] and prev_target[pi] is not None and prev_target[pi] != nearest:
            switch_ms = (now - prev_time[pi]) * 1000.0
            switch_deg = angle_between(prev_pitch[pi], prev_yaw[pi], pitch[ti, pi], yaw[ti, pi])

        # THE ARC. Every metric here measures a level, so a very good player scores like a bad
        # cheater and no threshold can sit anywhere - which is why "what do our best players hit"
        # has hung over every result. A slope escapes that: however good you are, a 120-degree
        # switch is harder than a 20-degree one and your hit rate falls off with the distance.
        # That is physiology, not skill. An aimbot's doesn't fall off; it already knows where the
        # target is, so the arc costs it nothing.
        #
        # This must be recorded for EVERY follow-up shot, hit or miss. The switch columns above
        # only exist when the shot was already on target, so binning their hit rate by angle is
        # circular - the denominator, the missed switches, isn't in the data at all. Asking about
        # a hit rate needs the misses.
        #
        # Denominator: you were engaged with A (last shot within ON_TARGET_DEG of them), then you
        # moved the view. Numerator: you landed on someone ELSE within 1 degree.
        arc_deg = -1.0
        arc_hit = 0
        if prev_on[pi] and prev_target[pi] is not None and 0 < now - prev_time[pi] < 2.0:
            arc_deg = angle_between(prev_pitch[pi], prev_yaw[pi], pitch[ti, pi], yaw[ti, pi])
            if nearest_err <= 1.0 and nearest != prev_target[pi]:
                arc_hit = 1

        # Dwell: how long this enemy had been under the crosshair before the trigger was pulled.
        dwell_ms = -1.0
        if on_target:
            if dwell_since[pi] < 0 or dwell_who[pi] != nearest:
                dwell_since[pi] = now
            dwell_ms = (now - dwell_since[pi]) * 1000.0
            dwell_who[pi] = nearest
        else:
            dwell_since[pi] = -1.0

        # Raw dwell cannot see a triggerbot, and the first run of this script proved it: 24% of
        # EVERYONE's shots land within 30ms of the target appearing under the crosshair. Those are
        # pre-fires and sprays — the crosshair is parked on a corner and the enemy walks into it,
        # which is 0ms dwell and entirely legitimate. The bot signal drowns in that.
        #
        # What separates them is who caused the crossing. Freeze the view at the previous tick and
        # re-measure against the target's CURRENT position: if the target was already under the
        # crosshair back then, they walked in (pre-fire). If it was well off and is on now, the
        # shooter swept onto them — and firing 0-16ms into your own sweep, without decelerating,
        # is the thing a hand cannot time and a trigger does every round.
        view_rate = frozen_err = -1.0
        burst_start = ti == 0 or not fired[ti - 1, pi]
        if ti > 0:
            dt = (uniq_ticks[ti] - uniq_ticks[ti - 1]) / TICKRATE
            if 0 < dt <= 0.1:
                view_rate = angle_between(pitch[ti - 1, pi], yaw[ti - 1, pi],
                                          pitch[ti, pi], yaw[ti, pi]) / dt
                frozen_err = aim_error_deg(eye, forward(pitch[ti - 1, pi], yaw[ti - 1, pi]),
                                           (X[ti, nearest], Y[ti, nearest], Z[ti, nearest]))

        prev_target[pi], prev_time[pi] = nearest, now
        prev_pitch[pi], prev_yaw[pi], prev_on[pi] = pitch[ti, pi], yaw[ti, pi], on_target

        name = uniq_players[pi]
        # "not a cheater" is two different claims mashed together. Inside a match where a cheater
        # WAS found, the dataset rates that label 55.6% precise - a coin flip. In a match where
        # none was found, nobody ever checked. Keeping them apart lets the cleaner negative be
        # used, and lets the two be compared against each other: if a coin-flip label and an
        # unverified one look identical, the label carries no information.
        rows.append(f"{os.path.basename(base)},{name},{1 if name in cheaters else 0},"
                    f"{nearest_err:.2f},{switch_ms:.0f},{switch_deg:.1f},{dwell_ms:.0f},"
                    f"{view_rate:.0f},{frozen_err:.1f},{1 if burst_start else 0},"
                    f"{arc_deg:.1f},{arc_hit},{1 if cheaters else 0}")

    # Per-player accuracy. Shots counted from the shots_fired counter, hits from the event log.
    match = os.path.basename(base)
    summary = []
    for pi in range(npl):
        name = uniq_players[pi]
        nshots = int(fired[:, pi].sum())
        if nshots == 0:
            continue
        nhits = hits.get(name, 0)
        summary.append(f"{match},{name},{1 if name in cheaters else 0},{nshots},"
                       f"{nhits},{heads.get(name, 0)}")
    return rows, summary


def analyse(path):
    import csv as _csv
    from collections import defaultdict
    # Bins of "how far did the crosshair have to travel to reach this new enemy" - the switch
    # angle is a difficulty scale, so keeping the bins apart lets the SHAPE be read, not just the
    # level. [shots, hits] per bin.
    ARC_BINS = ((15, 45), (45, 90), (90, 180))
    per = defaultdict(lambda: {"cheat": 0, "dirty": 0, "dwell": [], "switch": [],
                               "sweep": 0, "sweepHit": 0, "swSweep": 0, "swSweepHit": 0,
                               "arc": [[0, 0] for _ in ARC_BINS]})
    with open(path) as f:
        for r in _csv.DictReader(f):
            k = (r["match"], r["player"])
            p = per[k]
            p["cheat"] = int(r["isCheater"])
            p["dirty"] = int(r.get("matchHasCheater", 1))
            dw = float(r["onTargetMs"])
            if dw >= 0:
                p["dwell"].append(dw)
            sm, sd = float(r["switchMs"]), float(r["switchDeg"])
            if sm > 0 and sd > 15:
                p["switch"].append((sd, sm))

            # Sweep-through: a shot that opens a burst while the view is still travelling. The rate
            # gate excludes the pre-fire (parked crosshair, enemy walks in) and burstStart excludes
            # the spray -- the two things that fill the "0ms dwell" bucket for everyone.
            #
            # The question is then just: does it connect? Firing mid-sweep is firing blind, so a
            # hand lands few of them. A trigger only ever pulls when the crosshair is on a body,
            # so nearly all of its mid-sweep shots are on target. That ratio is the tell, and it
            # needs no assumption about how fast the bot reacts.
            # Independent of the sweep gate. Stacking arc on top of it collapsed the sample from
            # 13,000 shots to twelve - a broken measurement, not a null result.
            ad = float(r.get("arcDeg", -1))
            if ad >= 0:
                for bi, (lo, hi) in enumerate(ARC_BINS):
                    if lo <= ad < hi:
                        p["arc"][bi][0] += 1
                        p["arc"][bi][1] += int(r.get("arcHit", 0))
                        break

            vr = float(r["viewRateDegPerSec"])
            if vr >= SWEEP_RATE and int(r["burstStart"]):
                on = float(r["aimErrDeg"]) <= 1.0
                p["sweep"] += 1
                if on:
                    p["sweepHit"] += 1

                # Same shot, but only when it also switched to a DIFFERENT enemy. Raw switch SPEED
                # was dead - the cheaters crossed 90-180 deg slower than everyone else - because
                # there is no inhuman traverse rate to find. Asking about the landing instead: a
                # hand has to locate the new target before it can hit it, so connecting on the way
                # over is the hard part, not getting there fast.
                sd, sm = float(r["switchDeg"]), float(r["switchMs"])
                if sd > 15 and 0 < sm < 2000:
                    p["swSweep"] += 1
                    if on:
                        p["swSweepHit"] += 1

                    # Every metric so far has failed the same way: it measures a LEVEL, so a very
                    # good player looks like a bad cheater and the threshold has nowhere to sit.
                    # A slope doesn't have that problem. However good you are, a 120-degree switch
                    # is harder than a 20-degree one and your hit rate falls off with the arc. An
                    # aimbot's doesn't - it already knows where the target is, so distance costs
                    # it nothing. The ratio of far-hit-rate to near-hit-rate divides the skill out
                    # and leaves the shape, which is the part a human cannot fake.


    def q(v, x):
        v = sorted(v)
        return v[min(len(v) - 1, int(x * (len(v) - 1)))] if v else float("nan")

    # A median needs a real sample behind it. At 50 shots the "lowest dwell" list just ranks
    # whoever played least, which is how a rarely-seen regular once topped it.
    MIN = 200
    ch = [p for p in per.values() if p["cheat"] and len(p["dwell"]) >= MIN]
    cl = [p for p in per.values() if not p["cheat"] and len(p["dwell"]) >= MIN]
    # "Not a cheater" is two claims of very different quality: 55.6% precise inside a match where
    # someone WAS caught, versus never checked at all in a match where nobody was. Held apart, the
    # cleaner negative can be used - and the two compared against each other, which costs nothing
    # and says whether the label carries information at all.
    coin = [p for p in cl if p["dirty"]]
    unver = [p for p in cl if not p["dirty"]]
    print(f"players with {MIN}+ on-target shots:  cheaters={len(ch)}  non-cheaters={len(cl)}"
          f"  (of those: {len(coin)} labelled 'clean' in a cheater match [55.6% precise], "
          f"{len(unver)} from unverified clean matches)\n")

    print("=== dwell before firing (ms) ===")
    for lbl, grp in (("cheaters", ch), ("non-cheaters", cl)):
        med = [q(p["dwell"], 0.5) for p in grp]
        fast = [100.0 * sum(1 for d in p["dwell"] if d < 30) / len(p["dwell"]) for p in grp]
        if not med:
            print(f"  {lbl:14}: (none)")
            continue
        print(f"  {lbl:14} n={len(med):<4} median-of-medians={q(med, 0.5):6.0f}  "
              f"p10={q(med, 0.1):6.0f}  |  shots<30ms: median={q(fast, 0.5):.1f}%  p90={q(fast, 0.9):.1f}%")

    # The measurement raw dwell could not make: of the burst-opening shots fired while the view is
    # still travelling, how many are actually on a body? A hand fires those blind; a trigger only
    # fires them on target.
    print(f"\n=== sweep-through shots (burst start, view >={SWEEP_RATE:.0f} deg/s) ===")
    for lbl, grp in (("cheaters", ch), ("non-cheaters", cl)):
        g = [p for p in grp if p["sweep"] >= 20]
        if not g:
            print(f"  {lbl:14}: (none with 20+ sweep shots)")
            continue
        hit = [100.0 * p["sweepHit"] / p["sweep"] for p in g]
        pooled = 100.0 * sum(p["sweepHit"] for p in g) / sum(p["sweep"] for p in g)
        print(f"  {lbl:14} n={len(g):<4} shots={sum(p['sweep'] for p in g):<7} "
              f"on-target: pooled={pooled:5.1f}%  median={q(hit, 0.5):5.1f}%  "
              f"p90={q(hit, 0.9):5.1f}%  max={max(hit):5.1f}%")

    # Every metric here separates by 1.2-1.8x with the distributions sitting on top of each other,
    # so comparing medians says "unusable" and stops. But the plugin never accuses anyone -- it
    # raises a name for an admin to review. That makes recall cheap and false positives the only
    # real cost, so the question is not "how far apart are the middles" but "how far out is the
    # tail, and is anyone but cheaters in it".
    #
    # Threshold at the non-cheater p99 rather than the max: the non-cheater group is contaminated
    # (55.6% label precision), so its top end may well be cheaters, and calibrating to that would
    # be calibrating to the thing we are trying to find.
    print("\n=== recall at the non-cheater p99 threshold (what an admin-review tier would catch) ===")

    def tail(label, getter, need, unit="%"):
        # Threshold against the unverified-clean matches where available: nobody checked them, but
        # "nobody checked" beats a label the dataset itself rates at 55.6%, which is a coin flip.
        c = [getter(p) for p in per.values() if p["cheat"] and need(p)]
        pool = [p for p in per.values() if not p["cheat"] and not p["dirty"] and need(p)]
        if len(pool) < 20:
            pool = [p for p in per.values() if not p["cheat"] and need(p)]
        n = [getter(p) for p in pool]
        c = [x for x in c if x == x]
        n = [x for x in n if x == x]
        if len(c) < 20 or len(n) < 20:
            print(f"  {label:22}: (too few players)")
            return
        thr = q(n, 0.99)
        caught = 100.0 * sum(1 for x in c if x > thr) / len(c)
        fp = 100.0 * sum(1 for x in n if x > thr) / len(n)
        print(f"  {label:22} thr>{thr:6.1f}{unit}  catches {caught:5.1f}% of {len(c)} cheaters  "
              f"({fp:.1f}% of {len(n)} non-cheaters)")

    tail("sweep-through on-tgt", lambda p: 100.0 * p["sweepHit"] / p["sweep"] if p["sweep"] else float("nan"),
         lambda p: p["sweep"] >= 20)
    tail("  ..after a switch", lambda p: 100.0 * p["swSweepHit"] / p["swSweep"] if p["swSweep"] else float("nan"),
         lambda p: p["swSweep"] >= 10)

    # The slope itself: far hit rate / near hit rate. ~0 = falls apart over distance (a hand).
    # ~1 = distance is free (something that already knows where the target is).
    def flatness(p):
        near, far = p["arc"][0], p["arc"][-1]
        if near[0] < 5 or far[0] < 5 or near[1] == 0:
            return float("nan")
        return (far[1] / far[0]) / (near[1] / near[0])

    tail("arc flatness (far/near)", flatness, lambda p: True, "x")

    print(f"\n=== hit rate by switch arc: does accuracy survive the distance? ===")
    print(f"  {'group':22} {'arc':>8}  {'players':>7} {'shots':>7}  {'pooled hit%':>11}")
    for lbl, grp in (("cheaters", ch), ("clean-in-dirty (coinflip)", coin), ("unverified-clean", unver)):
        for bi, (lo, hi) in enumerate(ARC_BINS):
            sh = sum(p["arc"][bi][0] for p in grp)
            hi_ = sum(p["arc"][bi][1] for p in grp)
            npl = sum(1 for p in grp if p["arc"][bi][0] > 0)
            if sh == 0:
                continue
            print(f"  {lbl:22} {lo:3.0f}-{hi:3.0f}  {npl:7} {sh:7}  {100.0 * hi_ / sh:10.1f}%")
    print("  A hand should drop off across those rows. Anything that doesn't is not aiming by hand.")
    print("  If the two non-cheater groups look alike, the 55.6% label carries no information.")
    tail("shots under 30ms", lambda p: 100.0 * sum(1 for d in p["dwell"] if d < 30) / len(p["dwell"]),
         lambda p: len(p["dwell"]) >= MIN)

    print("\n=== target switch: ms to cross the angle ===")
    print("  group          bin        n     fastest    p1      p5     p50")
    for lbl, grp in (("cheaters", ch), ("non-cheaters", cl)):
        allsw = [s for p in grp for s in p["switch"]]
        for lo, hi in ((15, 45), (45, 90), (90, 180)):
            ms = sorted(m for dg, m in allsw if lo <= dg < hi)
            if len(ms) < 5:
                print(f"  {lbl:14} {lo:3}-{hi:3}  {len(ms):5}   (too few)")
                continue
            print(f"  {lbl:14} {lo:3}-{hi:3}  {len(ms):5}  {ms[0]:7.0f} {q(ms,0.01):7.0f} "
                  f"{q(ms,0.05):7.0f} {q(ms,0.5):7.0f}")

    # --- accuracy: the most direct signal we have ---
    acc_path = os.path.splitext(path)[0] + "_accuracy.csv"
    if os.path.exists(acc_path):
        agg = defaultdict(lambda: [0, 0, 0, 0])   # shots, hits, heads, isCheater
        with open(acc_path) as f:
            for r in _csv.DictReader(f):
                k = (r["match"], r["player"])
                a = agg[k]
                a[0] += int(r["shots"]); a[1] += int(r["hits"]); a[2] += int(r["headshots"])
                a[3] = int(r["isCheater"])
        MINS = 100
        print(f"\n=== accuracy (players with {MINS}+ shots) ===")
        for lbl, want in (("cheaters", 1), ("non-cheaters", 0)):
            accs = [100.0 * a[1] / a[0] for a in agg.values() if a[3] == want and a[0] >= MINS]
            hs = [100.0 * a[2] / max(1, a[1]) for a in agg.values() if a[3] == want and a[0] >= MINS and a[1] > 0]
            if not accs:
                print(f"  {lbl:14}: (none)")
                continue
            print(f"  {lbl:14} n={len(accs):<4} hit%: median={q(accs,0.5):5.1f}  p90={q(accs,0.9):5.1f}  "
                  f"max={max(accs):5.1f}  |  HS%: median={q(hs,0.5):5.1f}  p90={q(hs,0.9):5.1f}")

        # Same tail question as above: threshold above the non-cheaters and see who is left.
        print(f"\n  recall at the non-cheater p99 threshold:")
        for name, sel in (("hit%", lambda a: 100.0 * a[1] / a[0]), ("HS%", lambda a: 100.0 * a[2] / max(1, a[1]))):
            c = [sel(a) for a in agg.values() if a[3] == 1 and a[0] >= MINS and a[1] > 0]
            n = [sel(a) for a in agg.values() if a[3] == 0 and a[0] >= MINS and a[1] > 0]
            if len(c) < 20 or len(n) < 20:
                continue
            thr = q(n, 0.99)
            caught = 100.0 * sum(1 for x in c if x > thr) / len(c)
            fp = 100.0 * sum(1 for x in n if x > thr) / len(n)
            print(f"    {name:5} thr>{thr:5.1f}%  catches {caught:5.1f}% of {len(c)} cheaters  "
                  f"({fp:.1f}% of {len(n)} non-cheaters)")

    print("""
    The cheater label is the trustworthy half. Inside cheater matches the dataset only calls the
    "not cheater" label 55.6% precise, and the no-cheater matches were never verified - so the
    non-cheater group is contaminated, which can only blunt a real difference, never invent one.
    If these groups don't separate, the metric doesn't work.""")


def survey_labels(root):
    """What does the dataset actually claim about each cheater? Every conclusion drawn from this
    data so far has pooled all 335 of them together, which is only valid if they are all running
    the same thing. "No triggerbot signature exists" is a very different claim from "the five
    trigger users in here were drowned by 330 aimbots"."""
    from collections import Counter
    files = sorted(f for r, _, fs in os.walk(root) for f in fs if f.endswith(".json")
                   for f in [os.path.join(r, f)])
    keys = Counter()
    values = defaultdict(Counter)
    per_match_hist = Counter()
    n_cheaters = 0
    sample = None
    for path in files:
        try:
            with open(path) as fh:
                d = json.load(fh)
        except Exception:
            continue
        cs = d.get("cheaters", []) or []
        per_match_hist[len(cs)] += 1
        for c in cs:
            n_cheaters += 1
            if sample is None:
                sample = c
            if isinstance(c, dict):
                for k, v in c.items():
                    keys[k] += 1
                    if isinstance(v, (str, int, float, bool)):
                        values[k][str(v)[:40]] += 1
            else:
                keys[type(c).__name__] += 1
                values["<raw>"][str(c)[:40]] += 1

    print(f"{len(files)} json file(s), {n_cheaters} cheater entries\n")
    # 1309 cheaters across 796 files is 4.1 per match, which sits badly next to the README's
    # "317 matches with cheaters, 478 without". The split between a verified label and an
    # unverified one depends on that being right, so count it rather than trust it.
    print(f"cheaters per match: {per_match_hist.most_common()}")
    print(f"  matches with 0 cheaters: {per_match_hist.get(0, 0)}   with 1+: {sum(n for k, n in per_match_hist.items() if k)}\n")
    print(f"first entry verbatim:\n  {json.dumps(sample, ensure_ascii=False)[:500]}\n")
    print("fields present on cheater entries:")
    for k, n in keys.most_common():
        vs = values[k].most_common(8)
        rendered = ", ".join(f"{v}({n2})" for v, n2 in vs)
        print(f"  {k:24} {n:5}x   {rendered[:110]}")
    print("\nIf nothing here names a cheat TYPE, then every per-type claim made from this dataset -")
    print("including 'no triggerbot signature exists' - is unsupported, and the honest statement is")
    print("only that the metric doesn't separate the pooled group.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--out", default="cs2cd_shots.csv")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 2))
    ap.add_argument("--analyse", action="store_true")
    ap.add_argument("--labels", action="store_true",
                    help="report what the dataset says about each cheater (cheat type?)")
    a = ap.parse_args()

    if a.analyse:
        analyse(a.path)
        return
    if a.labels:
        survey_labels(a.path)
        return

    files = sorted(
        os.path.join(r, f)
        for r, _, fs in os.walk(a.path) for f in fs
        if f.endswith(".parquet") and os.path.exists(os.path.join(r, f[:-8] + ".json"))
    )
    if not files:
        sys.exit(f"no .parquet+.json pairs under {a.path}")
    print(f"{len(files)} match(es), {a.jobs} job(s)")

    acc_path = os.path.splitext(a.out)[0] + "_accuracy.csv"
    done = failed = 0
    with open(a.out, "w") as out, open(acc_path, "w") as accf, \
            ProcessPoolExecutor(max_workers=a.jobs) as ex:
        out.write("match,player,isCheater,aimErrDeg,switchMs,switchDeg,onTargetMs,"
                  "viewRateDegPerSec,frozenErrDeg,burstStart,arcDeg,arcHit,matchHasCheater\n")
        accf.write("match,player,isCheater,shots,hits,headshots\n")
        futs = {ex.submit(measure, f): f for f in files}
        for fut in as_completed(futs):
            try:
                shot_rows, acc_rows = fut.result()
                for line in shot_rows:
                    out.write(line + "\n")
                for line in acc_rows:
                    accf.write(line + "\n")
                done += 1
            except Exception as e:
                failed += 1
                print(f"\n[!] {os.path.basename(futs[fut])}: {e}", file=sys.stderr)
            out.flush(); accf.flush()
            print(f"\r  {done + failed}/{len(files)}  {failed} failed   ", end="", flush=True)

    print(f"\n\nWrote {a.out} and {acc_path}")
    print(f"Analyse with:  python {sys.argv[0]} --analyse {a.out}")


if __name__ == "__main__":
    main()
