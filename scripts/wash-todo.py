#!/usr/bin/env python3
"""Produce the public TODO.md from the private working copy.

The working lab log lives in private/TODO.md (gitignored) and may name players and
admins freely. This script washes it into the public TODO.md at the repo root:

  * every pattern in private/aliases.tsv is replaced by its pseudonym
    (C=cheater, G=griefer, T=temp-ban, R=regular, A=admin), longest pattern first;
  * every steamId64 (7656119...) is reduced to "[steamid]";
  * a banner explains the pseudonyms to public readers.

Run it after every edit to private/TODO.md, then commit the washed TODO.md:

    python3 scripts/wash-todo.py

It prints what it replaced and warns about anything that still looks like a leak,
so a new name that is missing from aliases.tsv gets caught before the commit.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PRIVATE = ROOT / "private" / "TODO.md"
ALIASES = ROOT / "private" / "aliases.tsv"
PUBLIC = ROOT / "TODO.md"

BANNER = """\
> **Public copy — pseudonymized.** Player and admin names are replaced with stable aliases
> (**C**n = typed cheater, **G**n = griefer, **T**n = temp-ban, **R**n = regular, **A**n = admin)
> and steamids are stripped; the working copy of this lab log lives outside the repo.
> Regenerate with `python3 scripts/wash-todo.py`.

"""


def main():
    if not PRIVATE.exists() or not ALIASES.exists():
        print(f"missing {PRIVATE} or {ALIASES}", file=sys.stderr)
        return 2

    rules = []
    for line in ALIASES.read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        pattern, _, alias = line.partition("\t")
        if pattern and alias:
            rules.append((pattern, alias.strip()))
    rules.sort(key=lambda r: -len(r[0]))

    text = PRIVATE.read_text(encoding="utf-8")
    for pattern, alias in rules:
        # Word-ish boundaries so e.g. "god" does not eat "goda"; punctuation-heavy
        # patterns (Bl@ck, [G]S ...) match literally.
        if re.fullmatch(r"[\wÅÄÖåäö]+", pattern):
            rx = re.compile(rf"(?<![\wÅÄÖåäö]){re.escape(pattern)}(?![\wÅÄÖåäö])")
        else:
            rx = re.compile(re.escape(pattern))
        text, n = rx.subn(alias, text)
        if n:
            print(f"  {pattern!r} -> {alias!r}  ×{n}")

    text, n = re.subn(r"7656119\d{10}", "[steamid]", text)
    if n:
        print(f"  steamid64 -> [steamid]  ×{n}")

    PUBLIC.write_text(BANNER + text, encoding="utf-8")
    print(f"wrote {PUBLIC}")

    # Leak check: any alias PATTERN still present in the output is a miss (an alias
    # that failed to apply everywhere), and any steamid is always a miss.
    leaks = [p for p, _ in rules
             if re.search(rf"(?<![\wÅÄÖåäö]){re.escape(p)}(?![\wÅÄÖåäö])", text)] \
        + (["steamid64"] if re.search(r"7656119\d{10}", text) else [])
    if leaks:
        print(f"\nWARNING — still present in the washed output: {leaks}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
