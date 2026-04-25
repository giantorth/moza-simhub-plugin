#!/usr/bin/env python3
"""Extract per-device JSON replay tables from a Moza USB pcapng capture.

Walks the capture pairing host→device requests with their device→host
responses (same logic as `ResponseReplay.load_pcapng`) and groups entries by
`(group, device)`. Writes one JSON file per (group, device) pair.

Usage:
    python3 sim/extract_replay.py <capture.pcapng> \\
        --out-dir sim/replay [--prefix kspro_] \\
        [--device 0x17] [--group 0x40]

Without filters, writes every (group, device) pair seen in the capture.
"""

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Dict, Tuple

_here = Path(__file__).parent
sys.path.insert(0, str(_here))
from wheel_sim import (  # noqa: E402
    extract_from_pcapng, verify, frame_payload, swap_nibbles,
)

PAIRING_WINDOW_SEC = 0.25

DEVICE_LABEL = {
    0x12: 'hub',
    0x13: 'base',
    0x14: 'brake_pedal',
    0x15: 'throttle_pedal',
    0x17: 'wheel',
    0x19: 'pedal',
    0x1b: 'shifter',
}


def pair_entries(path: str) -> Dict[Tuple[int, int], Dict[bytes, bytes]]:
    entries = extract_from_pcapng(path)
    n = len(entries)
    tables: Dict[Tuple[int, int], Dict[bytes, bytes]] = defaultdict(dict)

    for i in range(n):
        direction, ts, frame = entries[i]
        if direction != 'host' or not verify(frame) or len(frame) < 4:
            continue
        req_group = frame[2]
        req_device = frame[3]
        expected_rsp_group = req_group | 0x80
        expected_rsp_device = swap_nibbles(req_device)
        req_pl = frame_payload(frame)

        for j in range(i + 1, n):
            rsp_dir, rsp_ts, rsp_frame = entries[j]
            if rsp_ts - ts > PAIRING_WINDOW_SEC:
                break
            if rsp_dir != 'device' or not verify(rsp_frame) or len(rsp_frame) < 4:
                continue
            if (rsp_frame[2] == expected_rsp_group
                    and rsp_frame[3] == expected_rsp_device):
                rsp_pl = frame_payload(rsp_frame)
                # Match group 0x43 burst sub-command (matches load_pcapng logic).
                if (req_group == 0x43
                        and len(req_pl) >= 1 and len(rsp_pl) >= 1
                        and rsp_pl[0] != (req_pl[0] | 0x80)):
                    continue
                key = bytes(req_pl)
                if key not in tables[(req_group, req_device)]:
                    tables[(req_group, req_device)][key] = bytes(rsp_frame)
                break
    return tables


def parse_int(s: str) -> int:
    return int(s, 0)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('capture', help='Path to .pcapng')
    ap.add_argument('--out-dir', default='sim/replay',
                    help='Output directory (default: sim/replay)')
    ap.add_argument('--prefix', default='',
                    help='Filename prefix (e.g. "kspro_")')
    ap.add_argument('--device', type=parse_int, default=None,
                    help='Filter: only this device byte (e.g. 0x17)')
    ap.add_argument('--group', type=parse_int, default=None,
                    help='Filter: only this group byte (e.g. 0x40)')
    ap.add_argument('--label', default='',
                    help='Label to embed in each output file')
    args = ap.parse_args()

    cap_path = Path(args.capture)
    if not cap_path.exists():
        print(f'error: {cap_path} not found', file=sys.stderr)
        return 1

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    tables = pair_entries(str(cap_path))
    if not tables:
        print(f'warn: no replay entries extracted from {cap_path}', file=sys.stderr)
        return 1

    # Merge per device — one file per target device byte, group embedded in keys.
    per_device: Dict[int, Dict[str, str]] = defaultdict(dict)
    for (group, device), entries in tables.items():
        if args.device is not None and device != args.device:
            continue
        if args.group is not None and group != args.group:
            continue
        for req, rsp in entries.items():
            per_device[device][f'{group:02x}:{req.hex()}'] = rsp.hex()

    written = 0
    for device, merged in sorted(per_device.items()):
        dev_label = DEVICE_LABEL.get(device, f'dev{device:02x}')
        fname = f'{args.prefix}{dev_label}_{device:02x}.json'
        out_path = out_dir / fname
        data = {
            'schema': 1,
            'device': device,
            'label': args.label or f'{dev_label} (device 0x{device:02x})',
            'source': str(cap_path),
            'entries': dict(sorted(merged.items())),
        }
        with open(out_path, 'w') as f:
            json.dump(data, f, indent=2)
            f.write('\n')
        print(f'  wrote {out_path} ({len(merged)} entries across '
              f'{len({k.split(":")[0] for k in merged})} groups)')
        written += 1

    if written == 0:
        print('warn: no tables matched filters', file=sys.stderr)
        return 1
    print(f'\n{written} table(s) written to {out_dir}/')
    return 0


if __name__ == '__main__':
    sys.exit(main())
