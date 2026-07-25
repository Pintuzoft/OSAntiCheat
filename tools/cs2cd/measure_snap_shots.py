#!/usr/bin/env python3
"""aimbot.snap population read off DemoReplay's --shots export (the PER-SHOT channel).

The kill-anchored channel (archive6-kills) already gave the conjunction a true zero
baseline: prev >= 5deg -> fire <= 0.05deg never happened in 318k kills. But the
detector's unique value lives in the shots file, where kills never look:

  * mid-burst pulls -- the classic spray-aimbot drags ONE shot in the spray onto
    the head and lets the rest scatter, and
  * pulls onto a head behind a wall/smoke -- land exact, never become a kill.

The shots export is ~1 GB, too big to move. This script streams it once and leaves
two things: the population read on stdout, and a small extract CSV holding only the
rows worth a second look (exact-or-near landings, and any off->near approach), which
IS small enough to upload.

Columns consumed: steamId,name,demo,snapErrPrev,snapErrFire  (-1 = not computable:
tick gap, dead sample, no on-target enemy within 5deg, or point-blank).

usage: measure_snap_shots.py <shots.csv> [extract-out.csv]
"""
import csv
import sys
from collections import defaultdict

EXACT = 0.05    # deg -- the SnapDetector gate at the shot tick
OFF = 5.0       # deg -- the SnapDetector floor one tick before
# extract keeps anything that could inform the gate, well outside it in both directions:
KEEP_FIRE = 0.3     # every landing this precise, whatever the approach
KEEP_NEAR = 1.0     # plus any off->near approach (prev >= 3 landing <= 1deg)


def bin_of(v):
    for edge in (0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0):
        if v <= edge:
            return edge
    return 999.0


def main(path, out_path):
    total = 0
    comp = 0
    hist = defaultdict(int)          # snapErrFire, all computable
    hist_off = defaultdict(int)      # snapErrFire where prev >= OFF (the conjunction column)
    conj = []                        # the gate itself: prev >= OFF and fire <= EXACT
    per = defaultdict(lambda: {"name": "?", "comp": 0, "conj": 0})
    kept = 0

    # utf-8 both ways: Windows Python defaults to the ANSI code page and mangles names.
    with open(path, encoding="utf-8", errors="replace", newline="") as f, \
         open(out_path, "w", encoding="utf-8", newline="") as out:
        rd = csv.DictReader(f)
        keep_cols = [c for c in rd.fieldnames
                     if c in ("demo", "steamId", "name", "aimErrDeg", "headErrDeg",
                              "viewRateDegPerSec", "burstStart", "tick", "targetId",
                              "snapErrPrev", "snapErrFire")]
        w = csv.DictWriter(out, fieldnames=keep_cols, extrasaction="ignore")
        w.writeheader()

        for r in rd:
            total += 1
            try:
                fire = float(r["snapErrFire"])
                prev = float(r["snapErrPrev"])
            except (KeyError, TypeError, ValueError):
                continue
            if fire < 0 or prev < 0:
                continue
            comp += 1
            hist[bin_of(fire)] += 1
            p = per[r["steamId"]]
            p["name"] = r["name"]
            p["comp"] += 1
            if prev >= OFF:
                hist_off[bin_of(fire)] += 1
                if fire <= EXACT:
                    p["conj"] += 1
                    conj.append(dict(r))
            if fire <= KEEP_FIRE or (prev >= 3.0 and fire <= KEEP_NEAR):
                kept += 1
                w.writerow(r)

    print(f"shots total {total}; snap-computable {comp} ({100.0*comp/max(1,total):.1f}%)")

    print("\nsnapErrFire distribution (all computable / where prev >= "
          f"{OFF:.0f} deg = the conjunction column):")
    n_off = sum(hist_off.values())
    cum = cum_off = 0
    for edge in (0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 999.0):
        c = hist.get(edge, 0)
        co = hist_off.get(edge, 0)
        cum += c
        cum_off += co
        lbl = f"<={edge}" if edge != 999.0 else ">5"
        print(f"  {lbl:>7} deg: {c:8}  (cum {100.0*cum/max(1,comp):6.2f}%)   "
              f"off-approach: {co:6} (cum {100.0*cum_off/max(1,n_off):6.2f}%)")

    print(f"\nCONJUNCTION (prev >= {OFF} deg -> fire <= {EXACT} deg): {len(conj)} shots")
    for r in conj[:50]:
        print(f"  prev={float(r['snapErrPrev']):6.2f} -> fire={float(r['snapErrFire']):.3f}  "
              f"burstStart={r.get('burstStart','?')}  {r['name']}  {r['steamId']}  "
              f"[{r['demo']}] tick {r.get('tick','?')}")
    if len(conj) > 50:
        print(f"  ... {len(conj) - 50} more (all in the extract)")

    hitters = [(sid, p) for sid, p in per.items() if p["conj"] > 0]
    if hitters:
        print("\nplayers with conjunction hits (SnapMinSnaps=2 -> >=2 here = detector fires):")
        for sid, p in sorted(hitters, key=lambda kv: -kv[1]["conj"]):
            print(f"  {p['conj']:3} pulls / {p['comp']:6} shots  {p['name']}  {sid}")
    else:
        print("\nno player hits the conjunction anywhere in the file -- the per-shot")
        print("channel has the same true-zero baseline the kill channel showed.")

    print(f"\nextract: {kept} rows -> {out_path}  (upload this one)")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1],
                  sys.argv[2] if len(sys.argv) > 2 else "snap-extract.csv"))
