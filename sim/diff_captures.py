#!/usr/bin/env python3
"""Diff two normalized capture JSONs (produced by capture_session.py).

Usage:
    diff_captures.py BASELINE.json ACTUAL.json [--rate-threshold F]
                                                [--top N]

Reports:
  * Patterns in BASELINE not in ACTUAL (host missing on plugin side)
  * Patterns in ACTUAL not in BASELINE (host extras on plugin side)
  * Rate deltas above --rate-threshold per second (default 0.5)
  * First-occurrence-time delta (relative to window start)

Exit code:
    0  identical or within thresholds
    1  drift detected
"""
from __future__ import annotations

import argparse
import json
import sys


def load(path):
    with open(path) as f:
        return json.load(f)


def main() -> int:
    ap = argparse.ArgumentParser(description="Diff bridge captures.")
    ap.add_argument("baseline", help="reference (pithouse) capture JSON")
    ap.add_argument("actual", help="current (simhub) capture JSON")
    ap.add_argument("--rate-threshold", type=float, default=0.5,
                    help="abs rate delta /s to flag (default 0.5)")
    ap.add_argument("--top", type=int, default=30,
                    help="max rows in each section (default 30)")
    args = ap.parse_args()

    a = load(args.baseline)
    b = load(args.actual)

    sa = set(a["patterns"])
    sb = set(b["patterns"])
    only_a = sa - sb
    only_b = sb - sa
    shared = sa & sb

    print("=" * 70)
    print(f"Baseline: {a.get('source')} / {a.get('label')} "
          f"({a['frames']['total']} frames, {a['window']['duration_s']}s)")
    print(f"Actual:   {b.get('source')} / {b.get('label')} "
          f"({b['frames']['total']} frames, {b['window']['duration_s']}s)")
    print("=" * 70)

    drift = False

    print(f"\n## Patterns in BASELINE only ({len(only_a)}) — plugin should send these")
    miss_rows = []
    for k in only_a:
        miss_rows.append((a["by_grp_dev_cmd"][k]["rate_per_s"], k,
                          a["by_grp_dev_cmd"][k]["count"],
                          a["by_grp_dev_cmd"][k]["samples"][0] if a["by_grp_dev_cmd"][k]["samples"] else ""))
    miss_rows.sort(key=lambda x: -x[0])
    if miss_rows:
        drift = True
    for rate, key, n, sample in miss_rows[:args.top]:
        print(f"  {rate:6.2f}/s ({n:>5}x)  {key}  sample={sample[:60]}")

    print(f"\n## Patterns in ACTUAL only ({len(only_b)}) — plugin extras (may be wrong)")
    extra_rows = []
    for k in only_b:
        extra_rows.append((b["by_grp_dev_cmd"][k]["rate_per_s"], k,
                           b["by_grp_dev_cmd"][k]["count"],
                           b["by_grp_dev_cmd"][k]["samples"][0] if b["by_grp_dev_cmd"][k]["samples"] else ""))
    extra_rows.sort(key=lambda x: -x[0])
    if extra_rows:
        drift = True
    for rate, key, n, sample in extra_rows[:args.top]:
        print(f"  {rate:6.2f}/s ({n:>5}x)  {key}  sample={sample[:60]}")

    print(f"\n## Rate deltas on shared patterns (|Δ| ≥ {args.rate_threshold}/s)")
    delta_rows = []
    for k in shared:
        ra = a["by_grp_dev_cmd"][k]["rate_per_s"]
        rb = b["by_grp_dev_cmd"][k]["rate_per_s"]
        d = rb - ra  # positive = plugin sends more, negative = plugin sends less
        if abs(d) >= args.rate_threshold:
            delta_rows.append((d, k, ra, rb))
    delta_rows.sort(key=lambda x: -abs(x[0]))
    if delta_rows:
        drift = True
    for d, key, ra, rb in delta_rows[:args.top]:
        sign = "+" if d > 0 else ""
        print(f"  {sign}{d:6.2f}/s   {key:<35s} baseline={ra:6.2f}/s  actual={rb:6.2f}/s")

    print(f"\n## Summary")
    print(f"  baseline patterns: {len(sa)}")
    print(f"  actual patterns:   {len(sb)}")
    print(f"  missing on actual: {len(only_a)}")
    print(f"  extra on actual:   {len(only_b)}")
    print(f"  drifted shared:    {len(delta_rows)}")
    print(f"  drift = {drift}")

    return 1 if drift else 0


if __name__ == "__main__":
    sys.exit(main())
