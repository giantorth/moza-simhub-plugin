#!/usr/bin/env python3
"""Listen passively on the base's CDC ACM port for N seconds and print every
framed wheel→host message, grouped by (group, device, cmd-prefix).

Used to discover what spontaneous frames a real wheel emits — e.g. 7c:23
dashboard-activate notifications, telemetry, keepalives.
"""
from __future__ import annotations

import argparse
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from wheel_sim import MSG_START, verify, frame_payload  # type: ignore

import serial  # type: ignore


def _read_frame(ser: serial.Serial, deadline: float) -> bytes | None:
    while True:
        r = deadline - time.time()
        if r <= 0:
            return None
        ser.timeout = max(0.01, r)
        b = ser.read(1)
        if not b:
            return None
        if b[0] != MSG_START:
            continue
        ser.timeout = max(0.01, deadline - time.time())
        n_byte = ser.read(1)
        if not n_byte:
            return None
        n = n_byte[0]
        want = n + 3
        buf = bytearray()
        while len(buf) < want:
            r2 = deadline - time.time()
            if r2 <= 0:
                return None
            ser.timeout = max(0.01, r2)
            chunk = ser.read(want - len(buf))
            if not chunk:
                return None
            buf.extend(chunk)
        return bytes([MSG_START, n]) + bytes(buf)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('port')
    ap.add_argument('--seconds', type=float, default=5.0)
    args = ap.parse_args()

    ser = serial.Serial(args.port, baudrate=115200, timeout=0.05)
    deadline = time.time() + args.seconds
    counts: Counter = Counter()
    samples: dict = {}
    total = 0
    while time.time() < deadline:
        f = _read_frame(ser, deadline)
        if f is None:
            continue
        total += 1
        if not verify(f):
            counts[('bad_checksum',)] += 1
            continue
        if len(f) < 5:
            continue
        group = f[2]
        device = f[3]
        payload = frame_payload(f)
        cmd = bytes(payload[:2])
        key = (group, device, cmd.hex())
        counts[key] += 1
        if key not in samples:
            samples[key] = f.hex()
    ser.close()
    print(f'total frames: {total}')
    for key, n in counts.most_common():
        print(f'  {n:5d}  grp={key[0]:#04x} dev={key[1]:#04x} cmd={key[2]:<8}  sample={samples.get(key,"-")}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
