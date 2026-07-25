#!/usr/bin/env python3
"""Pull EVERY shot by a set of steamIds out of DemoReplay's --shots export.

The kill-anchored anatomy of the banned players already types two of them at
p < 1e-12 (silent aim: headshots landing with the demo-visible crosshair 15-37
degrees off the head). Their full shot logs -- misses included -- are the next
layer down: the banned nine fired ~700 shots total, so the extract is tiny even
though the shots export is ~1 GB.

usage: extract_shots_by_id.py <shots.csv> [out.csv] [steamId ...]
       (no ids given -> reads private/banned-ids.txt, one steamId64 per line,
        # comments allowed — the id list stays out of the public repo)
"""
import csv
import sys
from pathlib import Path

IDS_FILE = Path(__file__).resolve().parents[2] / "private" / "banned-ids.txt"


def default_ids():
    try:
        return [line.split("#")[0].strip()
                for line in IDS_FILE.read_text(encoding="utf-8").splitlines()
                if line.split("#")[0].strip()]
    except FileNotFoundError:
        return []


def main(path, out_path, ids):
    want = set(ids or default_ids())
    if not want:
        print(f"no steamIds: pass them as arguments or create {IDS_FILE}", file=sys.stderr)
        return 2
    kept = 0
    total = 0
    # utf-8 both ways: Windows Python defaults to the ANSI code page and mangles names.
    with open(path, encoding="utf-8", errors="replace", newline="") as f, \
         open(out_path, "w", encoding="utf-8", newline="") as out:
        rd = csv.reader(f)
        wr = csv.writer(out)
        header = next(rd)
        wr.writerow(header)
        sid = header.index("steamId")
        for row in rd:
            total += 1
            if len(row) > sid and row[sid] in want:
                wr.writerow(row)
                kept += 1
    print(f"{kept} shots by {len(want)} players (of {total} total) -> {out_path}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1],
                  sys.argv[2] if len(sys.argv) > 2 else "banned-shots.csv",
                  sys.argv[3:]))
