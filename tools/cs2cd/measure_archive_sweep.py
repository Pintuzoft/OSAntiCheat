#!/usr/bin/env python3
"""Establish where an ordinary honest player sits on sweep-through on-target.

The 16.1% threshold that gave sweep-through its 14x lift was read off random
matchmaking players. Our regulars are not that. Before that threshold can be
trusted here we need our OWN population baseline -- and the baseline has to
separate two things that look identical in a top list:

  * real spread between players (some genuinely land more mid-sweep shots), and
  * binomial noise (at 25 shots, 3/25 and 5/25 is luck, not behaviour).

Getting that wrong is how dwell got itself believed: that top list sorted on
sample size. Here it would sort on luck -- a legit player whose true rate is 11%
shows >16.1% about 40% of the time at n=20, purely by chance.

So this script reports three things:
  1. the pooled baseline (what an ordinary honest player actually hits),
  2. whether players genuinely differ at all (heterogeneity test), and
  3. what threshold our own data can justify -- not the imported one.

Input is DemoReplay's --shots export:
    demo,steamId,name,aimErrDeg,switchMs,switchDeg,onTargetMs,viewRateDegPerSec,burstStart

Gates are copied verbatim from measure_cs2cd.py so the numbers stay comparable:
a shot counts as a sweep-through when the view was still travelling >=90 deg/s
and the shot OPENED a burst (burstStart), which excludes the parked pre-fire and
the spray -- the two things that make everyone look fast. It is "on target" at
aimErr <= 1.0 deg.

Pooled PER PLAYER, never per session.
"""
import csv
import sys
from collections import defaultdict
from math import sqrt

SWEEP_RATE = 90.0       # deg/s -- view still travelling as the trigger went
ON_TARGET = 1.0         # deg -- shot landed on a body
CS2CD_THRESHOLD = 16.1  # % -- above the CS2CD non-cheaters' p99
Z = 1.96                # 95%


def wilson(k, n):
    """Wilson score interval. Honest at small n, where normal approximation is not:
    it never runs below 0 or above 1 and does not collapse to zero width at k=0."""
    if n == 0:
        return (0.0, 1.0)
    p = k / n
    d = 1 + Z * Z / n
    c = (p + Z * Z / (2 * n)) / d
    h = Z * sqrt(p * (1 - p) / n + Z * Z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def percentile(xs, q):
    if not xs:
        return float("nan")
    s = sorted(xs)
    i = (len(s) - 1) * q / 100.0
    lo = int(i)
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (i - lo)


def main(path, min_shots=200, top=25):
    per = defaultdict(lambda: {"name": "?", "sweep": 0, "hit": 0, "demos": set()})

    with open(path) as f:
        for r in csv.DictReader(f):
            vr = float(r["viewRateDegPerSec"])
            if vr < SWEEP_RATE or not int(r["burstStart"]):
                continue
            p = per[r["steamId"]]
            p["name"] = r["name"]
            p["demos"].add(r["demo"])
            p["sweep"] += 1
            if float(r["aimErrDeg"]) <= ON_TARGET:
                p["hit"] += 1

    elig = [(sid, p) for sid, p in per.items() if p["sweep"] >= min_shots]
    print(f"players seen: {len(per)}   with >={min_shots} sweep-through shots: {len(elig)}")

    if len(elig) < 5:
        print()
        print(f"NOT ENOUGH DATA. Only {len(elig)} player(s) cleared {min_shots} shots.")
        print("A baseline off fewer than a handful of well-sampled players is not a baseline.")
        best = sorted(per.values(), key=lambda p: -p["sweep"])[:5]
        print("\nbest-sampled players so far:")
        for p in best:
            lo, hi = wilson(p["hit"], p["sweep"])
            print(f"  {p['hit']:4}/{p['sweep']:<5} = {100.0*p['hit']/p['sweep']:5.1f}%  "
                  f"95% CI [{100*lo:4.1f}, {100*hi:5.1f}]  {len(p['demos']):3} demos  {p['name']}")
        print(f"\nRun more demos. At roughly 3-5 sweep shots per player per demo,")
        print(f"{min_shots} shots needs on the order of {min_shots // 4} demos per player.")
        return 1

    K = sum(p["hit"] for _, p in elig)
    N = sum(p["sweep"] for _, p in elig)
    pooled = K / N
    plo, phi = wilson(K, N)

    print()
    print("=" * 62)
    print("1. WHERE AN ORDINARY HONEST PLAYER SITS")
    print("=" * 62)
    print(f"pooled sweep-through on-target: {100*pooled:.2f}%  "
          f"95% CI [{100*plo:.2f}, {100*phi:.2f}]   ({K} on target / {N} shots)")

    rates = {sid: p["hit"] / p["sweep"] for sid, p in elig}
    vals = [100 * v for v in rates.values()]
    print("\nobserved per-player rate:")
    for q in (5, 25, 50, 75, 90, 95, 99):
        print(f"  p{q:<3} {percentile(vals, q):5.1f}%")
    print(f"  max  {max(vals):5.1f}%")

    print()
    print("=" * 62)
    print("2. DO PLAYERS GENUINELY DIFFER, OR IS THE SPREAD JUST NOISE?")
    print("=" * 62)
    # Chi-square heterogeneity: if every player shared one true rate, each count would
    # be binomial around the pooled rate. Spread beyond that is real between-player
    # variation. Without this test a top list cannot be read at all -- the ranking
    # might be nothing but who got lucky.
    chi = sum((p["hit"] - p["sweep"] * pooled) ** 2 / (p["sweep"] * pooled * (1 - pooled))
              for _, p in elig if p["sweep"] * pooled * (1 - pooled) > 0)
    df = len(elig) - 1
    ratio = chi / df if df else float("nan")
    print(f"chi-square {chi:.1f} on {df} df   -> variance ratio {ratio:.2f}")
    if ratio < 1.5:
        print("  ~1 means the spread is what one shared true rate would produce anyway.")
        print("  Players do NOT measurably differ. The top list is luck -- do not read it,")
        print("  and do not tune a threshold to the top name.")
    else:
        print("  >1 means players really do sit at different rates, so the spread is")
        print("  behaviour and the upper tail is worth a threshold.")

    print()
    print("=" * 62)
    print("3. WHAT THRESHOLD OUR OWN DATA JUSTIFIES")
    print("=" * 62)
    # The honest ceiling is the highest UPPER confidence bound among legit players:
    # a threshold under it can be tripped by a legit player we have simply not
    # sampled enough. Point estimates would set it too low and buy false positives.
    hi_bounds = [100 * wilson(p["hit"], p["sweep"])[1] for _, p in elig]
    ceiling = max(hi_bounds)
    print(f"highest legit point estimate : {max(vals):5.1f}%")
    print(f"highest legit 95% upper bound: {ceiling:5.1f}%   <- a threshold must clear THIS")
    print(f"imported CS2CD threshold     : {CS2CD_THRESHOLD:5.1f}%")
    if CS2CD_THRESHOLD > ceiling:
        print(f"\n  OK: {CS2CD_THRESHOLD}% sits above every legit player's upper bound "
              f"(margin {CS2CD_THRESHOLD - ceiling:.1f}pp).")
        print("  The imported threshold transfers to this population.")
    else:
        print(f"\n  WARNING: {CS2CD_THRESHOLD}% is INSIDE the legit range "
              f"(ceiling {ceiling:.1f}%). Legit players we have under-sampled could")
        print("  trip it. The threshold does NOT transfer -- raise it or drop the axis.")

    over = [sid for sid, v in rates.items() if 100 * v > CS2CD_THRESHOLD]
    print(f"\nplayers over {CS2CD_THRESHOLD}% on the point estimate: "
          f"{len(over)}/{len(elig)} = {100.0*len(over)/len(elig):.1f}%")
    print("  CS2CD read this as ~1% FP on random MM players.")

    print()
    print(f"top {top} -- SANITY-CHECK THESE NAMES against someone who knows the players.")
    print("  (the documented failure mode: the top list is just the best regulars)")
    for sid, _ in sorted(elig, key=lambda kv: -rates[kv[0]])[:top]:
        p = per[sid]
        lo, hi = wilson(p["hit"], p["sweep"])
        print(f"  {100*rates[sid]:5.1f}%  [{100*lo:4.1f},{100*hi:5.1f}]  "
              f"{p['hit']:5}/{p['sweep']:<6} {len(p['demos']):4} demos  "
              f"{p['name']:<24} {sid}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        print("usage: measure_archive_sweep.py <shots.csv> [min_shots] [top]")
        sys.exit(2)
    sys.exit(main(sys.argv[1],
                  int(sys.argv[2]) if len(sys.argv) > 2 else 200,
                  int(sys.argv[3]) if len(sys.argv) > 3 else 25))
