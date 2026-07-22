#!/usr/bin/env python3
"""Re-run the DETECTIONS report from an osac-replay sweep's CSVs — no re-parse.

The tuning loop this enables: run the archive ONCE (hours), then iterate
thresholds in seconds against the exported CSVs until known-legit players go
quiet. The tuned JSON is the future plugin config.

usage: report.py --players archive.csv --kills archive-kills.csv [--config thresholds.json]
"""
import argparse, csv, json, sys

DEFAULTS = {
    "deadaimMin": 0.05,
    "boneLockMinSpikes": 2,
    "antiRecoilMaxRatio": 0.04,
    "antiRecoilMinSprays": 6,
    "nullTestMinExcess": 0.02,
    "nullTestMinSamples": 2000,
}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--players", required=True, help="per-player CSV from --csv")
    ap.add_argument("--kills", help="per-kill CSV from --kills")
    ap.add_argument("--config", help="thresholds JSON (missing keys fall back to defaults)")
    ap.add_argument("--top", type=int, default=40, help="max rows per detector")
    a = ap.parse_args()

    cfg = dict(DEFAULTS)
    if a.config:
        with open(a.config) as f:
            for k, v in json.load(f).items():
                lk = next((d for d in DEFAULTS if d.lower() == k.lower()), k)
                cfg[lk] = v

    hits = {"deadaim": [], "bone-lock": [], "anti-recoil": [], "null-test": []}
    gated_all = []

    if a.kills:
        with open(a.kills, newline="") as f:
            for r in csv.DictReader(f):
                g = float(r["gatedSig"])
                gated_all.append(g)
                if g >= cfg["deadaimMin"]:
                    hits["deadaim"].append((g, f'{r["attackerName"]} -> {r["victimName"]}  '
                        f'{r["weapon"]} r{r["round"]} tick {r["tick"]}  [{r["demo"]}]  (raw {r["sig"]})'))

    with open(a.players, newline="") as f:
        for r in csv.DictReader(f):
            if int(r.get("headSpike", 0) or 0) >= cfg["boneLockMinSpikes"]:
                hits["bone-lock"].append((int(r["headSpike"]),
                    f'spike {r["headSpike"]}/{r["headN"]}  {r["name"]}  {r["steamId"]}  [{r["demo"]}]'))
            ratio, sprays = float(r.get("recoilRatio", -1) or -1), int(r.get("recoilSprays", 0) or 0)
            if 0 <= ratio <= cfg["antiRecoilMaxRatio"] and sprays >= cfg["antiRecoilMinSprays"]:
                hits["anti-recoil"].append((-ratio,
                    f'ratio {ratio:.3f} over {sprays} sprays  {r["name"]}  {r["steamId"]}  [{r["demo"]}]'))
            n = int(r.get("unseenSamples", 0) or 0)
            if n >= cfg["nullTestMinSamples"]:
                excess = (int(r["unseenNow"]) - int(r["unseenPast"])) / n
                if excess >= cfg["nullTestMinExcess"]:
                    hits["null-test"].append((excess,
                        f'excess {excess:.1%} (n={n})  {r["name"]}  {r["steamId"]}  [{r["demo"]}]'))

    if gated_all:
        gs = sorted(gated_all)
        pct = lambda p: gs[min(len(gs) - 1, int(p * (len(gs) - 1)))]
        print(f"deadaim gatedSig over {len(gs)} kills:  "
              f"p99 {pct(.99):.3f}  p99.9 {pct(.999):.3f}  p99.99 {pct(.9999):.3f}  max {gs[-1]:.3f}")

    print(f"\n=== DETECTIONS (config: {a.config or 'defaults'}) ===")
    print("  knobs: " + "  ".join(f"{k}={v}" for k, v in cfg.items()))
    total = 0
    for det, rows in hits.items():
        for _, line in sorted(rows, reverse=True)[: a.top]:
            total += 1
            print(f"  [{det:<11}] {line}")
        extra = len(rows) - a.top
        if extra > 0:
            print(f"  [{det:<11}] ... +{extra} more (raise the knob or --top)")
    if total == 0:
        print("  (no detections at these thresholds)")

if __name__ == "__main__":
    sys.exit(main())
