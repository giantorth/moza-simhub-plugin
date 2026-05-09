#!/usr/bin/env python3
"""Snapshot a slice of a bridge JSONL log into a normalized capture JSON.

Usage:
    capture_session.py LOG_PATH OUT_JSON [--from TS] [--to TS]
                       [--last SEC]  [--source NAME]
                       [--label LABEL]

Either pass `--from`/`--to` (epoch seconds, floats accepted) or `--last SEC`
to grab the trailing window of the log.

Output JSON shape:
    {
      "source":          "pithouse" | "simhub" | ...,
      "label":           "nebula-baseline" | ...,
      "log_path":        "...",
      "window":          { "start": float, "end": float, "duration_s": float },
      "frames":          { "total": N, "h2b": N, "b2h": N, "bad_checksum": N },
      "by_grp_dev_cmd":  { "h2b 0x40/0x17 1f03": { "count": N, "rate_per_s": F,
                                                   "first": ts, "last": ts,
                                                   "samples": [hex, hex, hex] }, ... },
      "patterns":        sorted list of "dir grp/dev cmd" keys for quick set ops
    }

The diff tool consumes this format.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path


def parse_window(args, last_t: float | None):
    if args.last is not None:
        if last_t is None:
            sys.exit("--last requires non-empty log")
        return last_t - args.last, last_t
    if args.from_ is None or args.to is None:
        sys.exit("must supply either --last SEC or both --from and --to")
    return args.from_, args.to


def main() -> int:
    ap = argparse.ArgumentParser(description="Snapshot a slice of a bridge JSONL log.")
    ap.add_argument("log_path", type=Path)
    ap.add_argument("out_json", type=Path)
    ap.add_argument("--from", dest="from_", type=float, default=None,
                    help="window start (epoch seconds)")
    ap.add_argument("--to", type=float, default=None,
                    help="window end (epoch seconds)")
    ap.add_argument("--last", type=float, default=None,
                    help="trailing window in seconds (alt to --from/--to)")
    ap.add_argument("--source", default="unknown",
                    help="data source tag (pithouse|simhub|...)")
    ap.add_argument("--label", default="",
                    help="free-form label for the run")
    ap.add_argument("--samples", type=int, default=3,
                    help="frame samples per pattern (default 3)")
    args = ap.parse_args()

    if not args.log_path.exists():
        sys.exit(f"log not found: {args.log_path}")

    # Pass 1: find last timestamp (for --last)
    last_t = None
    if args.last is not None:
        with args.log_path.open() as f:
            for line in f:
                try:
                    rec = json.loads(line)
                except Exception:
                    continue
                t = rec.get("t")
                if t is not None and (last_t is None or t > last_t):
                    last_t = t

    t0, t1 = parse_window(args, last_t)

    # Pass 2: aggregate
    counts = defaultdict(int)
    firsts = {}
    lasts = {}
    samples = defaultdict(list)
    total = h2b = b2h = bad = 0
    actual_start = None
    actual_end = None

    with args.log_path.open() as f:
        for line in f:
            try:
                rec = json.loads(line)
            except Exception:
                continue
            t = rec.get("t")
            if t is None or not (t0 <= t <= t1):
                continue
            if actual_start is None:
                actual_start = t
            actual_end = t
            total += 1
            d = rec.get("dir", "")
            if d == "h2b":
                h2b += 1
            elif d == "b2h":
                b2h += 1
            if not rec.get("ok", True):
                bad += 1
            grp = rec.get("grp")
            dev = rec.get("dev")
            pl = rec.get("payload", "") or ""
            if grp is None or dev is None:
                continue
            cmd = pl[:4] if len(pl) >= 4 else pl[:2]
            key = f"{d} 0x{grp:02X}/0x{dev:02X} {cmd}"
            counts[key] += 1
            if key not in firsts:
                firsts[key] = t
            lasts[key] = t
            if len(samples[key]) < args.samples:
                samples[key].append(rec.get("hex", ""))

    duration = (actual_end - actual_start) if (actual_start and actual_end) else 0.0
    by_kdc = {}
    for key, n in counts.items():
        by_kdc[key] = {
            "count": n,
            "rate_per_s": (n / duration) if duration > 0 else 0.0,
            "first": firsts[key],
            "last": lasts[key],
            "samples": samples[key],
        }

    out = {
        "source": args.source,
        "label": args.label,
        "log_path": str(args.log_path),
        "window": {
            "requested_start": t0,
            "requested_end": t1,
            "actual_start": actual_start,
            "actual_end": actual_end,
            "duration_s": round(duration, 3),
        },
        "frames": {
            "total": total, "h2b": h2b, "b2h": b2h, "bad_checksum": bad,
        },
        "by_grp_dev_cmd": by_kdc,
        "patterns": sorted(by_kdc.keys()),
    }
    args.out_json.write_text(json.dumps(out, indent=2))
    print(f"wrote {args.out_json}: {total} frames, {len(by_kdc)} patterns, "
          f"duration {duration:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
