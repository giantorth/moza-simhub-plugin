#!/usr/bin/env python3
"""Probe a real MOZA wheel over the base's CDC ACM serial port to extract the
identity bytes needed for a WHEEL_MODELS entry in wheel_sim.py.

Usage:
    python3 sim/probe_wheel.py /dev/ttyACM0

Sends PitHouse-style identity queries for the wheel (device 0x17) and the
display sub-device (group 0x43), plus a few base probes. Prints the raw
response bytes, ASCII decode, and a ready-to-paste WHEEL_MODELS fragment.
"""
from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from wheel_sim import (  # type: ignore
    build_frame, verify, frame_payload,
    MSG_START, GRP_HOST, GRP_WHEEL, DEV_WHEEL, DEV_WHEEL_RSP,
)

import serial  # type: ignore


# (group, device, payload, label, response_group_expected)
# Wheel identity probes — PitHouse sends these bare (group N → device 0x17).
WHEEL_PROBES = [
    (0x07, DEV_WHEEL, b'\x01',                 'name'),
    (0x0F, DEV_WHEEL, b'\x01',                 'sw_version'),
    (0x08, DEV_WHEEL, b'\x01',                 'hw_version'),
    (0x08, DEV_WHEEL, b'\x02',                 'hw_sub'),
    (0x10, DEV_WHEEL, b'\x00',                 'serial0'),
    (0x10, DEV_WHEEL, b'\x01',                 'serial1'),
    (0x05, DEV_WHEEL, b'\x00\x00\x00\x00',     'caps'),
    (0x06, DEV_WHEEL, b'',                     'hw_id'),
    (0x11, DEV_WHEEL, b'\x04',                 'identity-11'),
    (0x09, DEV_WHEEL, b'',                     'presence'),
    (0x02, DEV_WHEEL, b'',                     'product-type'),
    (0x04, DEV_WHEEL, b'\x00\x00\x00\x00',     'dev_type'),
]

# Display sub-device probes — group 0x43, nested cmd bytes.
DISPLAY_PROBES = [
    (GRP_HOST, DEV_WHEEL, b'\x09',             'disp-presence'),
    (GRP_HOST, DEV_WHEEL, b'\x02',             'disp-product-type'),
    (GRP_HOST, DEV_WHEEL, b'\x04\x00\x00\x00', 'disp-dev_type'),
    (GRP_HOST, DEV_WHEEL, b'\x05\x00\x00\x00', 'disp-caps'),
    (GRP_HOST, DEV_WHEEL, b'\x06',             'disp-hw_id'),
    (GRP_HOST, DEV_WHEEL, b'\x07\x01',         'disp-name'),
    (GRP_HOST, DEV_WHEEL, b'\x08\x01',         'disp-hw_version'),
    (GRP_HOST, DEV_WHEEL, b'\x08\x02',         'disp-hw_sub'),
    (GRP_HOST, DEV_WHEEL, b'\x0f\x01',         'disp-sw_version'),
    (GRP_HOST, DEV_WHEEL, b'\x0f\x02',         'disp-sw_sub'),
    (GRP_HOST, DEV_WHEEL, b'\x10\x00',         'disp-serial0'),
    (GRP_HOST, DEV_WHEEL, b'\x10\x01',         'disp-serial1'),
    (GRP_HOST, DEV_WHEEL, b'\x11\x04',         'disp-identity-11'),
]


def _read_frame(ser: serial.Serial, deadline: float) -> bytes | None:
    """Read one framed 7E...checksum frame from ser, honoring a wall-clock deadline."""
    while True:
        remaining = deadline - time.time()
        if remaining <= 0:
            return None
        ser.timeout = max(0.01, remaining)
        b = ser.read(1)
        if not b:
            return None
        if b[0] != MSG_START:
            continue
        # length byte
        ser.timeout = max(0.01, deadline - time.time())
        n_byte = ser.read(1)
        if not n_byte:
            return None
        n = n_byte[0]
        want = n + 3  # group + device + payload(n) + checksum
        buf = bytearray()
        while len(buf) < want:
            r = deadline - time.time()
            if r <= 0:
                return None
            ser.timeout = max(0.01, r)
            chunk = ser.read(want - len(buf))
            if not chunk:
                return None
            buf.extend(chunk)
        frame = bytes([MSG_START, n]) + bytes(buf)
        return frame


def probe(port: str, timeout_s: float = 0.8) -> dict:
    ser = serial.Serial(port, baudrate=115200, timeout=0.05)
    # Drain any in-flight traffic (base emits heartbeats/telemetry).
    t_drain_end = time.time() + 0.3
    while time.time() < t_drain_end:
        ser.read(4096)
    results = {}
    for probe_list, section in [(WHEEL_PROBES, 'wheel'), (DISPLAY_PROBES, 'display')]:
        for group, device, payload, label in probe_list:
            frame = build_frame(group, device, payload)
            ser.reset_input_buffer()
            ser.write(frame)
            ser.flush()
            expect_group = group | 0x80
            deadline = time.time() + timeout_s
            matched = None
            # Collect up to ~20 frames in the window looking for our match.
            received = []
            while time.time() < deadline:
                f = _read_frame(ser, deadline)
                if f is None:
                    break
                received.append(f)
                if len(f) >= 4 and f[2] == expect_group:
                    matched = f
                    break
            key = f'{section}:{label}'
            if matched is not None:
                p = frame_payload(matched)
                results[key] = {
                    'req': frame.hex(),
                    'rsp_frame': matched.hex(),
                    'rsp_payload': p.hex(),
                    'rsp_ascii': ''.join(chr(b) if 32 <= b < 127 else '.' for b in p),
                }
            else:
                results[key] = {
                    'req': frame.hex(),
                    'rsp_frame': None,
                    'received_any': [f.hex() for f in received[:3]],
                }
            time.sleep(0.02)
    ser.close()
    return results


def main() -> int:
    ap = argparse.ArgumentParser(description='Probe real MOZA wheel identity.')
    ap.add_argument('port', help='Serial port (e.g. /dev/ttyACM0)')
    ap.add_argument('--timeout', type=float, default=0.8,
                    help='Per-probe response timeout (s)')
    args = ap.parse_args()

    results = probe(args.port, timeout_s=args.timeout)
    for key, r in results.items():
        if r.get('rsp_frame'):
            print(f'[OK ] {key:32s} rsp={r["rsp_payload"]}  ascii="{r["rsp_ascii"]}"')
        else:
            print(f'[MISS] {key:32s} (no matching group returned)')
    print()
    print('# Raw JSON dump:')
    import json
    print(json.dumps(results, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
