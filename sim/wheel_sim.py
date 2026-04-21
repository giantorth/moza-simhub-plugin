#!/usr/bin/env python3
"""
MOZA Wheel Simulator — virtual MOZA wheel with display for debugging dashboard telemetry.

Acts as the wheel side of the MOZA serial protocol: responds to session opens, tier
definitions, probes, and identity queries so the plugin or PitHouse can proceed to
send 7D:23 telemetry frames. Decodes and displays received telemetry in real time.

Runs on Linux or Windows. Live mode uses pyserial — `pip install pyserial`.

Invocation:

    python3 sim/wheel_sim.py <port>                           # live mode
    python3 sim/wheel_sim.py --validate <capture.pcapng>      # offline decode
    python3 sim/wheel_sim.py --replay-handshake <capture>     # self-test
    python3 sim/wheel_sim.py --replay-self-test <capture>     # replay-table sanity

Setup guides (authoritative):
  - docs/SIMULATOR.md   — architecture, tty0tty/com0com live-mode setup,
                          replay-table behaviour, capture workflow.
  - sim/README.md       — per-model `WHEEL_MODELS` profile reference; what
                          each field maps to, how PitHouse detects the
                          wheel/display, and the "Adding a new model" recipe
                          with extraction helpers.
  - sim/USBIP_SETUP.md  — USBIP bridge for exposing the sim as a real USB VGS
                          wheel (VID 0x346E PID 0x0006) to a Windows host so
                          PitHouse enumerates it.
"""

import argparse
import collections
import json
import struct
import subprocess
import zlib
import sys
import threading
import time
from pathlib import Path
from typing import Deque, Dict, List, Optional, Tuple

# ── Protocol constants ──────────────────────────────────────────────────────

MSG_START = 0x7E
CHECKSUM_SEED = 0x0D

GRP_HOST = 0x43     # host → wheel (TelemetrySendGroup)
GRP_WHEEL = 0xC3    # wheel → host (GRP_HOST | 0x80)

DEV_WHEEL = 0x17    # host addresses wheel with this device ID
DEV_WHEEL_RSP = 0x71  # wheel responds with this (nibble-swapped: 0x17 → 0x71)

SESSION_TYPE_OPEN = 0x81   # 7C:00 session open request
SESSION_TYPE_DATA = 0x01   # 7C:00 session data chunk
SESSION_TYPE_END = 0x00    # 7C:00 session end / close marker

# Device-initiated sessions observed across moza-startup, connect-wheel-start-game,
# and dash-upload captures. The real wheel opens these after the host has opened
# 0x01/0x02; PitHouse then uses them for file transfer (0x04), keepalives
# (0x06/0x08/0x0a), and the configJson RPC that populates the Dashboard Manager
# UI (0x09). Without these opens the wheel's dashboard list never reaches
# PitHouse. See docs/moza-protocol.md § Session lifecycle for the full mapping.
#
# `port` equals the session byte for every device-opened session observed
# (0x04→4, 0x06→6, 0x08→8, 0x09→9, 0x0a→10). Host-opened 0x03 uses port 0x0a,
# which we leave to the host side.
_DEVICE_SESSIONS: List[Tuple[int, int, float]] = [
    # (session, port, delay_seconds_after_phase_start)
    (0x04, 0x04, 0.15),   # file transfer (mzdash upload target)
    (0x06, 0x06, 0.17),   # host→device keepalive
    (0x08, 0x08, 0.20),   # device↔host keepalive
    (0x09, 0x09, 0.20),   # configJson RPC (dashboard state)
    (0x0a, 0x0a, 0.22),   # device→host keepalive
]
_DEVICE_SESSION_RETRY_SEC = 1.0
_DEVICE_SESSION_MAX_RETRIES = 3

DISPLAY_PROBE_CMD = 0x07
DISPLAY_IDENTITY_CMD = 0x87
DISPLAY_SUBDEV = 0x01

# Synthetic acks for plugin ProbeMozaDevice() — ProbeMozaDevice only checks
# first byte == 0x7E, so any framed echo works. (group, device) → (rsp_group, rsp_dev).
_PROBE_SYNTH: Dict[Tuple[int, int], Tuple[int, int]] = {
    (0x2B, 0x13): (0xAB, 0x31),   # base probe  (group|0x80, swap_nibbles(0x13))
    (0x64, 0x12): (0xE4, 0x21),   # hub probe
}

# Wheel-write cmd-prefixes where the real wheel echoes the full request payload
# back as its response. Observed in captures for group 0x3F/0x3E to dev 0x17:
#   req  7e N 3F 17 XX YY ...  →  rsp  7e N BF 71 XX YY ... (echoed verbatim)
# Covers per-LED color, channel-enable, brightness, page-config writes whose
# data bytes vary per call (LED index, channel CC, brightness level) so the
# payload-keyed replay table can't cover them.
_SIMULATED_DEVICES: set = {0x12, 0x13, 0x17}  # hub, base, wheel

_WHEEL_ECHO_PREFIXES: set = {
    (0x3F, 0x17, b'\x1f\x00'),  # per-LED color page 0
    (0x3F, 0x17, b'\x1f\x01'),  # per-LED color page 1
    (0x3F, 0x17, b'\x1e\x00'),  # channel CC enable page 0
    (0x3F, 0x17, b'\x1e\x01'),  # channel CC enable page 1
    (0x3F, 0x17, b'\x1b\x00'),  # brightness page 0
    (0x3F, 0x17, b'\x1b\x01'),  # brightness page 1
    (0x3F, 0x17, b'\x1c\x00'),  # page config
    (0x3F, 0x17, b'\x1d\x00'),  # page config
    (0x3F, 0x17, b'\x1d\x01'),  # page config
    (0x3F, 0x17, b'\x27\x00'),  # LED display config page 0
    (0x3F, 0x17, b'\x27\x01'),  # LED display config page 1
    (0x3F, 0x17, b'\x27\x02'),  # LED display config page 2
    (0x3F, 0x17, b'\x27\x03'),  # LED display config page 3
    (0x3F, 0x17, b'\x2a\x00'),
    (0x3F, 0x17, b'\x2a\x01'),
    (0x3F, 0x17, b'\x2a\x02'),
    (0x3F, 0x17, b'\x2a\x03'),
    (0x3F, 0x17, b'\x0a\x00'),
    (0x3F, 0x17, b'\x24\xff'),  # display setting
    (0x3F, 0x17, b'\x20\x01'),
    (0x3F, 0x17, b'\x1a\x00'),  # RPM LED telemetry write
    (0x3F, 0x17, b'\x19\x00'),  # RPM LED color write
    (0x3F, 0x17, b'\x19\x01'),  # button LED color write
    (0x3E, 0x17, b'\x0b'),      # newer-wheel LED cmd (1-byte prefix)
}

def _id_str(s: str) -> bytes:
    """16-byte null-padded ASCII identity string."""
    return s.encode('ascii')[:16].ljust(16, b'\x00')

# ── Wheel model profiles ──────────────────────────────────────────────────
# Each profile defines the identity strings and protocol details for a
# specific wheel model. Selected via --model CLI arg (default: vgs).

WHEEL_MODELS: Dict[str, dict] = {
    'vgs': {
        'name': 'VGS',
        'friendly': 'Vision GS',
        'sw_version': 'RS21-W08-MC SW',
        'hw_version': 'RS21-W08-HW SM-C',
        'hw_sub': 'U-V12',
        # Wheel serials — zeroed placeholders (real serials redacted).
        'serial0': 'VGS00000000000',
        'serial1': '00000000000000',
        'caps': bytes([0x01, 0x02, 0x1f, 0x01]),
        'hw_id': bytes.fromhex('be4930021471350430303337'),
        # Real VGS emits 3 7c:23 page frames on connect (see
        # connect-wheel-start-game.pcapng). Different byte layout than CSP.
        'emits_7c23': True,
        '_7c23_frames_name': 'VGS',
        'session_layout': 'vgs_combined',
        # Replay real-hardware session 1/2 frames from this capture instead of
        # synthesizing. Real VGS session 2 has more than the 5 description
        # chunks — it continues with model-specific TLVs that PitHouse needs
        # before it will send the full tier definition on session 1.
        'catalog_pcapng': 'usb-capture/connect-wheel-start-game.pcapng',
        # Device description blob — split into 5 TLV-aligned sub-messages by
        # build_device_catalog's vgs_combined layout (chunk sizes 26/5/2/9/2).
        # Byte-for-byte match with connect-wheel-start-game.pcapng session 2
        # seq 5..9 data.
        'session1_desc': bytes.fromhex(
            '0701000000000c0669420714e806e0df1099ff3404100105'  # 24B
            '0a06'                                               # last 2B of chunk 1 (→26)
            '0164000000'                                         # chunk 2 (5B)
            '0500'                                               # chunk 3 (2B)
            '040000000000000000'                                 # chunk 4 (9B)
            '0600'                                               # chunk 5 (2B)
        ),
        'display': {
            'name': 'Display',
            'sw_version': 'RS21-W08-HW SM-D',
            'hw_version': 'RS21-W08-HW SM-D',
            'hw_sub': 'U-V14',
            # Serials redacted. Display hw_id extracted from connect-wheel-start-game.pcapng
            # (real VGS + PitHouse) — needed for PitHouse to correctly identify the display.
            'serial0': 'VGSDISPLAY000000',
            'serial1': 'VGSDISPLAY000001',
            'dev_type': bytes([0x01, 0x02, 0x08, 0x06]),
            'caps': bytes([0x01, 0x02, 0x00, 0x00]),
            'hw_id': bytes.fromhex('694207 14e8 06e0 df10 99ff 34'.replace(' ', '')),
        },
    },
    'ks': {
        # MOZA KS race wheel (RS21-W04). Integrated button LEDs, no detachable
        # display — caps byte 2 = 0x1a has no 0x20 bit. Identity bytes captured
        # 2026-04-20 from real hardware via sim/probe_wheel.py against R5 base.
        'name': 'KS',
        'friendly': 'KS',
        'sw_version': 'RS21-W04-MC SW',
        'hw_version': 'RS21-W04-HW SM-C',
        'hw_sub': 'U-V04B',
        # Serials redacted.
        'serial0': 'KS00000000000000',
        'serial1': 'KS00000000000001',
        'caps': bytes([0x01, 0x02, 0x1a, 0x00]),
        'hw_id': bytes.fromhex('450053000a51343033363539'),
        # Real KS returns cmd-echo 04 + 00, not 04:01 like VGS/CSP. Must match
        # or PitHouse mis-identifies the wheel (see VGS comment above).
        'identity_11': bytes([0x04, 0x00]),
        # KS uses sub-byte 0x05 in dev_type where VGS/CSP use 0x04.
        'dev_type': bytes([0x01, 0x02, 0x05, 0x06]),
        # No dashboard screen — doesn't emit 7c:23 page-activate frames and
        # doesn't need a session1_desc/catalog replay.
        'emits_7c23': False,
        'session_layout': 'legacy',
    },
    'csp': {
        'name': 'W17',
        'friendly': 'CS Pro',
        'sw_version': 'RS21-W17-MC SW',
        'hw_version': 'RS21-W17-HW SM-C',
        'hw_sub': 'U-V12',
        # Serials redacted.
        'serial0': 'CSP0000000000000',
        'serial1': 'CSP0000000000001',
        'caps': bytes([0x01, 0x02, 0x3f, 0x01]),
        'hw_id': bytes.fromhex('80313bc000203004'),
        'emits_7c23': True,
        '_7c23_frames_name': 'CSP',
        'session1_desc': bytes.fromhex(
            '0701000000000c048ae5d086b2fcad7486dbe208041001'
            '0a0164000000050004020000000000000006 00'
            .replace(' ', '')),
        'display': {
            'name': 'W17 Display',
            'sw_version': 'RS21-W17-HW RGB-',
            'hw_version': 'RS21-W17-HW RGB-',
            'hw_sub': 'DU-V11',
            # Serials redacted.
            'serial0': 'CSPDISPLAY000000',
            'serial1': 'CSPDISPLAY000001',
            'dev_type': bytes([0x01, 0x02, 0x0d, 0x06]),
            'caps': bytes([0x01, 0x02, 0x00, 0x00]),
            'hw_id': bytes.fromhex('8ae5d086b2fcad7486dbe208'),
        },
    },
}

def _build_identity_tables(model: dict) -> Tuple[
    Dict[Tuple[int, int, bytes], bytes],
    Dict[Tuple[int, bytes], bytes],
]:
    """Build plugin probe and PitHouse identity tables from a wheel model profile."""
    name = model['name']
    disp = model.get('display', {})

    # Plugin probe responses — device-independent entries plus wheel identity
    plugin_rsp: Dict[Tuple[int, int, bytes], bytes] = {
        # ── Wheel detection (MozaDeviceManager.ProbeWheelDetection → ids 23/21/19) ──
        (0x40, 0x17, b'\x28\x00'): b'\x28\x00\x01',
        (0x40, 0x15, b'\x28\x00'): b'\x28\x00\x01',
        (0x40, 0x13, b'\x28\x00'): b'\x28\x00\x01',
        (0x40, 0x17, b'\x24\x00'): b'\x24\x00\x03\xE8',
        (0x40, 0x15, b'\x24\x00'): b'\x24\x00\x03\xE8',
        (0x40, 0x13, b'\x24\x00'): b'\x24\x00\x03\xE8',
        # ── StatusPollCommands (base status) ──
        (0x2B, 0x13, b'\x01'): b'\x01\x00\x00',
        (0x2B, 0x13, b'\x02'): b'\x02\x00\x00',
        (0x2B, 0x13, b'\x04'): b'\x04\x01\x2C',
        (0x2B, 0x13, b'\x05'): b'\x05\x01\x2C',
        (0x2B, 0x13, b'\x06'): b'\x06\x01\x2C',
        # ── Dashboard detection ──
        (0x33, 0x14, b'\x11\x00'): b'\x11\x00\x01',
        # ── Config-mode probe ──
        (0x3F, 0x17, b'\x09\x32'): b'\x09\x28',
        (0x3F, 0x17, b'\x09\x00'): b'\x09\x28',
        # ── Main hub settings write ──
        (0x1F, 0x12, b'\x4e\x08'): b'\x4c\x00',
        (0x1F, 0x12, b'\x4e\x09'): b'\x4c\x00',
        (0x1F, 0x12, b'\x4e\x0a'): b'\x4c\x00',
        (0x1F, 0x12, b'\x4e\x0b'): b'\x4c\x00',
        # ── Wheel identity reads (model-specific) ──
        (0x07, 0x17, b'\x01'): b'\x01' + _id_str(name),
        (0x0F, 0x17, b'\x01'): b'\x01' + _id_str(model['sw_version']),
        (0x08, 0x17, b'\x01'): b'\x01' + _id_str(model['hw_version']),
        (0x08, 0x17, b'\x02'): b'\x02' + _id_str(model['hw_sub']),
        (0x10, 0x17, b'\x00'): b'\x00' + _id_str(model['serial0']),
        (0x10, 0x17, b'\x01'): b'\x01' + _id_str(model['serial1']),
    }

    # Display sub-device identity probes (routed via group 0x43 to device 0x17).
    # Always installed when the model has a `display` block — the SimHub plugin's
    # SendDisplayProbe sends all of these regardless of wheel model, and relies
    # on the 0x07:0x01 response (handled by the _handle_wheel display_probe
    # branch) to detect the display. PitHouse only triggers this probe cascade
    # when the wheel's capability byte signals a detachable RGB display
    # (CSP caps byte 2 = 0x3f, VGS = 0x1f — no 0x20 bit, so PitHouse skips).
    if disp:
        plugin_rsp.update({
            (0x43, 0x17, b'\x09'):             bytes([0x89, 0x00, 0x01]),
            (0x43, 0x17, b'\x02'):             bytes([0x82, 0x02]),
            (0x43, 0x17, b'\x04\x00\x00\x00'): bytes([0x84]) + disp['dev_type'],
            (0x43, 0x17, b'\x05\x00\x00\x00'): bytes([0x85]) + disp['caps'],
            (0x43, 0x17, b'\x06'):             bytes([0x86]) + disp['hw_id'],
            (0x43, 0x17, b'\x0f\x01'):         bytes([0x8f, 0x01]) + _id_str(disp['sw_version']),
            (0x43, 0x17, b'\x0f\x02'):         bytes([0x8f, 0x02]) + _id_str(disp.get('hw_sub', '')),
            (0x43, 0x17, b'\x08\x01'):         bytes([0x88, 0x01]) + _id_str(disp['hw_version']),
            (0x43, 0x17, b'\x08\x02'):         bytes([0x88, 0x02]) + _id_str(disp.get('hw_sub', '')),
            (0x43, 0x17, b'\x10\x00'):         bytes([0x90, 0x00]) + _id_str(disp['serial0']),
            (0x43, 0x17, b'\x10\x01'):         bytes([0x90, 0x01]) + _id_str(disp['serial1']),
            (0x43, 0x17, b'\x11\x04'):         bytes([0x91, 0x04, 0x01]),
        })

    # PitHouse identity probes (groups 0x02–0x11, device 0x17)
    pithouse_rsp: Dict[Tuple[int, bytes], bytes] = {
        (0x09, b''):                 bytes([0x00, 0x01]),
        (0x02, b''):                 bytes([0x02]),
        # dev_type varies per wheel — VGS/CSP real HW returns 01:02:04:06,
        # KS returns 01:02:05:06. Default matches VGS/CSP.
        (0x04, b'\x00\x00\x00\x00'): model.get('dev_type', bytes([0x01, 0x02, 0x04, 0x06])),
        (0x05, b'\x00\x00\x00\x00'): model['caps'],
        (0x06, b''):                 model['hw_id'],
        (0x07, b'\x01'):             _id_str(name),
        (0x08, b'\x01'):             _id_str(model['hw_version']),
        (0x08, b'\x02'):             _id_str(model['hw_sub']),
        (0x0F, b'\x01'):             _id_str(model['sw_version']),
        (0x10, b'\x00'):             _id_str(model['serial0']),
        (0x10, b'\x01'):             _id_str(model['serial1']),
        # Real VGS/CSP wheels return cmd-echo + 0x01 here. Sim returned 00:00
        # for a long time — PitHouse tolerated it for CSP but mis-identified
        # VGS (probably "feature flags" value PitHouse consults to decide
        # whether to probe the display sub-device path). KS real HW returns
        # cmd-echo + 0x00 instead — per-model override in WHEEL_MODELS.
        (0x11, b'\x04'):             model.get('identity_11', bytes([0x04, 0x01])),
    }

    return plugin_rsp, pithouse_rsp

# Built at startup from selected --model. Populated by main().
_PLUGIN_PROBE_RSP: Dict[Tuple[int, int, bytes], bytes] = {}
_PITHOUSE_ID_RSP: Dict[Tuple[int, bytes], bytes] = {}
_DISPLAY_MODEL_NAME: str = 'Display'

# Semantic labels for unhandled-frame logging. Drawn from docs/moza-protocol.md
# and Protocol/MozaProtocol.cs group constants.
_GROUP_LABELS: Dict[int, str] = {
    0x00: 'heartbeat',
    0x02: 'device presence/version probe',
    0x04: 'device type probe',
    0x05: 'capability flags probe',
    0x06: 'hardware-id read',
    0x07: 'wheel model-name identity',
    0x08: 'wheel HW-version identity',
    0x09: 'presence/ready probe',
    0x0E: 'firmware debug console',
    0x0F: 'wheel SW-version identity',
    0x10: 'wheel serial identity',
    0x11: 'wheel identity-11',
    0x1F: 'main hub settings',
    0x23: 'pedals settings read',
    0x24: 'pedals settings write',
    0x28: 'base settings read',
    0x29: 'base settings write',
    0x2B: 'base telemetry read',
    0x2D: 'sequence-counter',
    0x33: 'dash settings read',
    0x3F: 'wheel RPM/button LED telemetry',
    0x40: 'wheel settings read',
    0x41: 'telemetry enable',
    0x43: 'telemetry main-stream',
    0x5B: 'handbrake settings read',
    0x5C: 'handbrake settings write',
    0x64: 'hub settings read',
}

# Precise (group, device, cmd-prefix) labels. Longest-prefix wins during lookup.
_CMD_LABELS: Dict[Tuple[int, int, bytes], str] = {
    # Plugin wheel-detection probes
    (0x40, 0x17, b'\x28\x00'): 'wheel-telemetry-mode probe (id 23)',
    (0x40, 0x15, b'\x28\x00'): 'wheel-telemetry-mode probe (id 21)',
    (0x40, 0x13, b'\x28\x00'): 'wheel-telemetry-mode probe (id 19)',
    (0x40, 0x17, b'\x24\x00'): 'wheel-rpm-value1 probe (id 23)',
    (0x40, 0x15, b'\x24\x00'): 'wheel-rpm-value1 probe (id 21)',
    (0x40, 0x13, b'\x24\x00'): 'wheel-rpm-value1 probe (id 19)',
    # Base status polls
    (0x2B, 0x13, b'\x01'): 'base-state read',
    (0x2B, 0x13, b'\x02'): 'base-state-err read',
    (0x2B, 0x13, b'\x04'): 'base-mcu-temp read',
    (0x2B, 0x13, b'\x05'): 'base-mosfet-temp read',
    (0x2B, 0x13, b'\x06'): 'base-motor-temp read',
    # Peripheral detection
    (0x33, 0x14, b'\x11\x00'): 'dash-rpm-indicator-mode probe',
    (0x5B, 0x1B, b'\x01'):     'handbrake-direction probe',
    (0x23, 0x19, b'\x01'):     'pedals-throttle-dir probe',
    (0x64, 0x12, b'\x03'):     'hub-port1-power probe',
    # Display sub-device identity probes (via group 0x43)
    (0x43, 0x17, b'\x09'):     'display sub-dev presence probe',
    (0x43, 0x17, b'\x02'):     'display sub-dev product type',
    (0x43, 0x17, b'\x04\x00'): 'display sub-dev device type',
    (0x43, 0x17, b'\x05\x00'): 'display sub-dev capability',
    (0x43, 0x17, b'\x06'):     'display sub-dev HW ID',
    (0x43, 0x17, b'\x08\x01'): 'display sub-dev HW version',
    (0x43, 0x17, b'\x08\x02'): 'display sub-dev HW sub-version',
    (0x43, 0x17, b'\x0f\x01'): 'display sub-dev SW version',
    (0x43, 0x17, b'\x0f\x02'): 'display sub-dev SW sub-version',
    (0x43, 0x17, b'\x10\x00'): 'display sub-dev serial 0',
    (0x43, 0x17, b'\x10\x01'): 'display sub-dev serial 1',
    (0x43, 0x17, b'\x11\x04'): 'display sub-dev identity-11',
    # Telemetry / session related (high-frequency once detection completes)
    (0x41, 0x17, b'\xFD\xDE'): 'dash telemetry enable flag',
    (0x3F, 0x17, b'\x1A\x00'): 'RPM LED telemetry write',
    (0x3F, 0x17, b'\x1A\x01'): 'button LED telemetry write',
    (0x3F, 0x17, b'\x19\x00'): 'RPM LED color write',
    (0x3F, 0x17, b'\x19\x01'): 'button LED color write',
    (0x2D, 0x13, b'\xF5\x31'): 'base sequence counter',
    (0x43, 0x17, b'\x7D\x23'): 'telemetry 7D:23 stream',
    (0x43, 0x17, b'\x7C\x00'): 'telemetry session frame',
    (0x43, 0x17, b'\x7C\x1E'): 'display settings push',
    (0x43, 0x17, b'\x7C\x23'): 'dashboard-activate notify',
    (0x43, 0x17, b'\x7C\x27'): 'display-config page cycle',
    (0x43, 0x17, b'\xFC\x00'): 'telemetry session ack',
    (0x43, 0x17, b'\x07\x01'): 'display sub-device identity probe',
    # Bare 0x43 connection-level keepalive pings (n=1, payload=0x00; device replies 0x80)
    (0x43, 0x14, b'\x00'): 'dash keepalive',
    (0x43, 0x15, b'\x00'): 'wheel-21 keepalive',
    (0x43, 0x17, b'\x00'): 'wheel keepalive',
    # Wheel LED/config writes (group 0x3F to dev 0x17) — wheel echoes request payload
    (0x3F, 0x17, b'\x1f\x00'): 'per-LED color write page 0',
    (0x3F, 0x17, b'\x1f\x01'): 'per-LED color write page 1',
    (0x3F, 0x17, b'\x1e\x00'): 'channel enable page 0',
    (0x3F, 0x17, b'\x1e\x01'): 'channel enable page 1',
    (0x3F, 0x17, b'\x1b\x00'): 'brightness page 0',
    (0x3F, 0x17, b'\x1b\x01'): 'brightness page 1',
    (0x3F, 0x17, b'\x1c\x00'): 'page config 1c:00',
    (0x3F, 0x17, b'\x1d\x00'): 'page config 1d:00',
    (0x3F, 0x17, b'\x1d\x01'): 'page config 1d:01',
    (0x3F, 0x17, b'\x24\xff'): 'display setting write',
    (0x3E, 0x17, b'\x0b'):     'newer-wheel LED cmd 0b',
    # Heartbeat (group 0x00, empty payload, one per device ID)
    (0x00, 0x12, b''): 'heartbeat → hub',
    (0x00, 0x13, b''): 'heartbeat → base',
    (0x00, 0x14, b''): 'heartbeat → dash',
    (0x00, 0x15, b''): 'heartbeat → wheel id 21',
    (0x00, 0x17, b''): 'heartbeat → wheel id 23',
    (0x00, 0x19, b''): 'heartbeat → pedals',
    (0x00, 0x1B, b''): 'heartbeat → handbrake',
}

def annotate(group: int, device: int, payload: bytes) -> str:
    """Human label for an otherwise-unhandled frame. Longest cmd-prefix wins;
    falls back to group-level label, then bare hex."""
    for plen in (4, 3, 2, 1, 0):
        if len(payload) >= plen:
            hit = _CMD_LABELS.get((group, device, bytes(payload[:plen])))
            if hit is not None:
                return hit
    grp_label = _GROUP_LABELS.get(group)
    cmd = payload[:2].hex(' ') if len(payload) >= 2 else payload.hex(' ')
    if grp_label:
        return f'{grp_label} [cmd={cmd} dev=0x{device:02X}]'
    return f'unknown grp=0x{group:02X} dev=0x{device:02X} cmd={cmd}'

# Bit widths per compression type (pithouse-re.md § 8)
COMP_BITS: Dict[str, int] = {
    'bool': 1,
    'uint3': 4, 'uint8': 4, 'uint15': 4,
    'int30': 5, 'uint30': 5, 'uint31': 5,
    'int8_t': 8, 'uint8_t': 8,
    'percent_1': 10, 'float_001': 10,
    'tyre_pressure_1': 12,
    'tyre_temp_1': 14, 'track_temp_1': 14, 'oil_pressure_1': 14,
    'uint16_t': 16, 'int16_t': 16,
    'float_6000_1': 16, 'float_600_2': 16, 'brake_temp_1': 16,
    'uint24_t': 24,
    'float': 32, 'int32_t': 32, 'uint32_t': 32,
    'double': 64, 'location_t': 64, 'int64_t': 64, 'uint64_t': 64,
}

# ── Checksum ────────────────────────────────────────────────────────────────

def checksum(data: bytes) -> int:
    """(0x0D + sum_of_bytes) % 256"""
    return (CHECKSUM_SEED + sum(data)) % 256

def verify(frame: bytes) -> bool:
    """Verify frame checksum.  The sender computes checksum AFTER escaping
    0x7E bytes in the body, so each 0x7E in the decoded body (positions
    2..-2) adds an extra 0x7E to the wire-level sum."""
    if len(frame) < 2:
        return False
    escaped_extra = frame[2:-1].count(MSG_START) * MSG_START
    return frame[-1] == (checksum(frame[:-1]) + escaped_extra) % 256

def build_frame(group: int, device: int, payload: bytes) -> bytes:
    """Assemble 7E [N] [group] [device] [payload: N bytes] [checksum].
    Checksum is computed on the wire representation (after 0x7E escaping)."""
    n = len(payload)
    buf = bytes([MSG_START, n, group, device]) + payload
    escaped_extra = buf[2:].count(MSG_START) * MSG_START
    ck = (checksum(buf) + escaped_extra) % 256
    return buf + bytes([ck])

# Proactive 7c:23 dashboard-activate notification frames.
# Real wheel sends these on connection to tell PitHouse the wheel has a
# dashboard loaded. Payloads extracted byte-for-byte from real-hardware
# captures — CSP emits 2 page variants, VGS emits 3. Byte 2 varies between
# wheels (CSP page 1 = 0x3c, VGS page 1 = 0x32).
_7C_23_FRAMES_CSP = [
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x3c\x80\x03\x00\x01\x00\xfe\x01'),  # CSP page 1
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x32\x80\x04\x00\x02\x00\xfe\x01'),  # CSP page 2
]
_7C_23_FRAMES_VGS = [
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x32\x80\x03\x00\x01\x00\xfe\x01'),  # VGS page 1
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x3c\x80\x04\x00\x02\x00\xfe\x01'),  # VGS page 2
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x50\x80\x05\x00\x03\x00\xfe\x01'),  # VGS page 3
]
# Default (legacy alias for backward compat)
_7C_23_FRAMES = _7C_23_FRAMES_CSP

# Dashboard-upload device reply: recorded from usb-capture/09-04-26/dash-upload.pcapng.
# After PitHouse finishes uploading a .mzdash file (FF-prefixed sub-messages on a
# management session), the wheel responds with this stream — an identity field,
# a small identity2 field, a multi-chunk compressed response, and a closing ack.
# Replaying this verbatim unblocks PitHouse; the specific bytes encode wheel
# identity + upload confirmation and appear to be accepted without per-session
# token rewriting. Format: list of raw chunk bytes (net data, no CRC32 trailer
# — we regenerate CRC when framing). First entry is sent ~0ms after upload end;
# remaining entries are paced to roughly match the original capture cadence.
_DASH_UPLOAD_REPLY_CHUNKS: List[bytes] = [
    bytes.fromhex("ff0c000000ec77d9e60a00000060ea0000000000000dbcc402"),
    bytes.fromhex("ff14000000053d3a99100000000000476a0100000000904d95010000004a135412"),
    bytes.fromhex("ffe60200001fbdf97f0e00000000001cca789ced99cd6ed34010c7ff7d050e9c573dc1a11f769336c90d095240b4452d2a125555a5756b4fd775"),
    bytes.fromhex("5a22354d1527fd142fc07370e2c89103478e3c044fc26fd66e24dc1445806a47b2acf5eecceeccfe67bc3b3bd94872d2ccb276f446ef5bd75581"),
    bytes.fromhex("35847aa253f5a99dea94807743214f4315adc009b54859d62e326bdad03b249eaaa518f95dbda5d5d7893a9423649c9a703a3a565b117bf54b79"),
    bytes.fromhex("d4403dde31b58d1bd0b6d97a3aa03fe6713a67f4005d4e2fd0eb34ab6bedf3d498355095d903cda1ab0e869096a1aa53e6d05845cb22b7abe33e"),
    bytes.fromhex("ad15df6f5415b93a4f8886aa3ea0cd7954b3f45d500e740602e3aca7c886f06cf6c314b5836ffe88a0fb5e62e03997e8283df7d79ed3e14e2ece"),
    bytes.fromhex("a7093d57cb78ee99bada44db097adb4825feb29e9b5173d415ff1ef71de6a103ea5dfa76c0d4f75eebd06a81dab8af69991f6324a41fba03c9af"),
    bytes.fromhex("ff19a70399d3021a7b8c68d3ead2ba62ce05efa5186a48ebc0f745e9fa30fe025f751d9d2ed56aa35ab4929177f7cd33c3d54893f4adc3137fa8"),
    bytes.fromhex("401635fd8ae8d27f0cdf108ce3652df852200b5641e2b4ed77d7efd474a00e33d474a05eca5059d43f0b84fa257b730de955b843b459f33f6795"),
    bytes.fromhex("64dc22c25ca651325939938c9a6e2bc309474db7954b138eca5839f3a04056bea2ef8cf63188dd2847d9876bb947e4cff8e46cdf66be56e9c120"),
    bytes.fromhex("98d26356b377eb1fa58b7c566dfa0c21b1ea26dedfe6652df85e200bba72634fdcf1fcac25f9666cb532632b844565c69637ea3263cba0b0c217"),
    bytes.fromhex("ff942f33b632632b33b632632b76c6f6b54096f499bf8bbcad1cf3fe6d4e767f7dcc25dfac8123a00ed27cd3a46df51b75e473b47de86e676608"),
    bytes.fromhex("bef740d3ef91868f1576fb7973177be8bf49b2c662246c6fd92e3af55e3a43675be7d01d74b4d26893dcefde8f6ff7c010f20ef0c01edb15d461"),
    bytes.fromhex("916d8fbaca9853ef29a747de0f8bba48ebc7aca5cf397e8de53fded79ac6d8eb1e680364316be7618e68edbefd6eb443d044fef74a5d649ede"),
    bytes.fromhex("7b14691bf7b8ab9ee7e819fb67e1f6ae6a8efe2f88efdc45913f9ddade6b5141f6499e11aae23195116a14a17e013e2f9868d9ce39d4"),
    bytes.fromhex("00000000"),
]
# Delay after last FF-prefix upload chunk before sending the reply stream.
_DASH_UPLOAD_REPLY_IDLE_MS = 300

# ── Frame parsing ────────────────────────────────────────────────────────────

def frame_len(n: int) -> int:
    """Total wire bytes for a frame with N payload bytes: 7E(1)+N(1)+group(1)+dev(1)+N+cksum(1)."""
    return 5 + n

def parse_frames(data: bytes) -> List[bytes]:
    """Extract all complete Moza frames from a contiguous byte buffer.

    Handles byte-stuffing: any 0x7E in the frame body is doubled on the wire.
    We decode (collapse 0x7E 0x7E → 0x7E) while scanning.
    """
    frames = []
    i = 0
    while i < len(data):
        if data[i] != MSG_START:
            i += 1
            continue
        if i + 1 >= len(data):
            break
        n = data[i + 1]
        need = n + 3  # group + device + payload(n) + checksum
        decoded = bytearray()
        j = i + 2  # position after start + N
        while len(decoded) < need and j < len(data):
            if data[j] == MSG_START:
                if j + 1 < len(data) and data[j + 1] == MSG_START:
                    decoded.append(MSG_START)
                    j += 2
                else:
                    break  # bare 0x7E = next frame start, current frame truncated
            else:
                decoded.append(data[j])
                j += 1
        if len(decoded) < need:
            break
        frames.append(bytes([MSG_START, n]) + bytes(decoded))
        i = j
    return frames

def read_one_frame(ser) -> Optional[bytes]:
    """Read exactly one Moza frame from a pyserial port (blocking).

    Handles MOZA byte-stuffing: any 0x7E in the frame body (group, device,
    payload, or checksum) is doubled on the wire (0x7E → 0x7E 0x7E).  The
    reader must collapse each pair back to a single 0x7E while counting
    *decoded* bytes against the expected length.
    See: https://github.com/Lawstorant/boxflat/pull/131
    """
    while True:
        b = ser.read(1)
        if not b:
            return None
        if b[0] == MSG_START:
            break
    nb = ser.read(1)
    if not nb:
        return None
    n = nb[0]
    # Need group(1) + device(1) + payload(n) + checksum(1) = n+3 decoded bytes
    decoded = bytearray()
    need = n + 3
    while len(decoded) < need:
        raw = ser.read(1)
        if not raw:
            return None
        if raw[0] == MSG_START:
            esc = ser.read(1)
            if not esc:
                return None
            if esc[0] == MSG_START:
                decoded.append(MSG_START)
            else:
                return None
        else:
            decoded.append(raw[0])
    return bytes([MSG_START, n]) + bytes(decoded)

def frame_payload(frame: bytes) -> bytes:
    """Return N payload bytes (cmd + data), excluding group/device/checksum."""
    n = frame[1]
    return frame[4:4 + n]

# ── Telemetry.json loading ──────────────────────────────────────────────────

def load_telemetry_db() -> Dict[str, dict]:
    """Load Data/Telemetry.json → {url_suffix: {compression, package_level}}."""
    here = Path(__file__).parent
    for candidate in [here.parent / 'Data' / 'Telemetry.json',
                      here / '..' / 'Data' / 'Telemetry.json']:
        p = candidate.resolve()
        if p.exists():
            with open(p) as f:
                data = json.load(f)
            return {
                s['url'].split('/')[-1]: {
                    'url': s['url'],
                    'compression': s.get('compression', 'uint8_t'),
                    'package_level': s.get('package_level', 30),
                }
                for s in data.get('sectors', [])
                if 'url' in s
            }
    print('[WARN] Data/Telemetry.json not found — channel names will be unknown',
          file=sys.stderr)
    return {}

# ── Session packet log ──────────────────────────────────────────────────────

def _ts() -> str:
    t = time.time()
    return time.strftime('%H:%M:%S', time.localtime(t)) + f'.{int((t % 1) * 1000):03d}'

def _rotate_session_log(log_path: Path, keep: int = 5) -> None:
    """Keep last `keep` sessions (current + keep-1 history). Shifts .N→.N+1, drops overflow."""
    log_path.parent.mkdir(parents=True, exist_ok=True)
    history = keep - 1
    overflow = log_path.with_suffix(f'.log.{history}')
    if overflow.exists():
        overflow.unlink()
    for i in range(history - 1, 0, -1):
        src = log_path.with_suffix(f'.log.{i}')
        if src.exists():
            src.replace(log_path.with_suffix(f'.log.{i + 1}'))
    if log_path.exists():
        log_path.replace(log_path.with_suffix('.log.1'))

def _open_session_log(log_path: Path, port: str):
    _rotate_session_log(log_path)
    fh = open(log_path, 'w', buffering=1)
    started = time.strftime('%Y-%m-%d %H:%M:%S')
    fh.write(f'# wheel_sim session started {started} port={port}\n')
    return fh

# ── LSB-first bit reader ────────────────────────────────────────────────────

class BitReader:
    def __init__(self, data: bytes):
        self._data = data
        self._pos = 0  # bit position

    def read_bits(self, count: int) -> int:
        value = 0
        bits_done = 0
        pos = self._pos
        while bits_done < count:
            byte_off = pos // 8
            bit_off = pos % 8
            if byte_off >= len(self._data):
                break
            take = min(count - bits_done, 8 - bit_off)
            chunk = (self._data[byte_off] >> bit_off) & ((1 << take) - 1)
            value |= chunk << bits_done
            bits_done += take
            pos += take
        self._pos = pos
        return value

    @property
    def bit_pos(self) -> int:
        return self._pos

# ── Compression decoders (pithouse-re.md § 9) ───────────────────────────────

def decode_value(compression: str, raw: int) -> float:
    c = compression.lower()
    if c == 'bool':
        return float(raw & 1)
    if c in ('uint3', 'uint8', 'uint15', 'uint30', 'uint31', 'int30'):
        return float(raw)
    if c == 'uint8_t':
        return float(raw & 0xFF)
    if c == 'int8_t':
        v = raw & 0xFF
        return float(v - 256 if v >= 128 else v)
    if c == 'percent_1':
        return float('nan') if raw == 1023 else raw / 10.0
    if c == 'float_001':
        return float('nan') if raw == 1023 else raw / 1000.0
    if c == 'tyre_pressure_1':
        return raw / 10.0
    if c in ('tyre_temp_1', 'track_temp_1', 'oil_pressure_1'):
        return (raw - 5000) / 10.0
    if c == 'uint16_t':
        return float(raw & 0xFFFF)
    if c == 'int16_t':
        v = raw & 0xFFFF
        return float(v - 65536 if v >= 32768 else v)
    if c == 'float_6000_1':
        return raw / 10.0
    if c == 'float_600_2':
        return raw / 100.0
    if c == 'brake_temp_1':
        return (raw - 5000) / 10.0
    if c == 'uint24_t':
        return float(raw & 0xFFFFFF)
    if c == 'float':
        return struct.unpack('<f', struct.pack('<I', raw & 0xFFFFFFFF))[0]
    if c in ('int32_t', 'uint32_t'):
        return float(raw & 0xFFFFFFFF)
    if c in ('double', 'location_t', 'int64_t', 'uint64_t'):
        return struct.unpack('<d', struct.pack('<Q', raw & 0xFFFFFFFFFFFFFFFF))[0]
    return float(raw)

def decode_telemetry(data_bytes: bytes, channels: List[dict]) -> Dict[str, float]:
    """Decode a 7D:23 payload section into named channel values."""
    reader = BitReader(data_bytes)
    result = {}
    for ch in channels:
        comp = ch['compression']
        bits = ch['bit_width']
        if comp in ('double', 'location_t', 'int64_t', 'uint64_t'):
            lo = reader.read_bits(32)
            hi = reader.read_bits(32)
            raw = lo | (hi << 32)
        else:
            raw = reader.read_bits(bits)
        result[ch['name']] = decode_value(comp, raw)
    return result

# ── Tier definition parsers ─────────────────────────────────────────────────

def parse_v0_tier_def(data: bytes, db: Dict[str, dict]) -> List[dict]:
    """
    Parse a v0 URL-subscription tier def (what the plugin sends to the wheel).
    Format: 0xFF sentinel, 0x03 config, 0x04 channel entries, 0x06 end.
    Returns channels sorted by 1-based index (== alphabetical URL order).
    """
    channels = []
    i = 0
    while i < len(data):
        tag = data[i]
        if tag == 0xFF:
            i += 1
            continue
        if i + 5 > len(data):
            break
        param_size = struct.unpack_from('<I', data, i + 1)[0]
        if param_size > 0xFFFF:  # sanity check
            break
        if tag == 0x04 and param_size >= 1 and i + 5 + param_size <= len(data):
            ch_index = data[i + 5]
            url = data[i + 6:i + 5 + param_size].decode('ascii', errors='replace')
            suffix = url.split('/')[-1]
            entry = db.get(suffix, {})
            comp = entry.get('compression', 'uint8_t')
            channels.append({
                'index': ch_index,
                'name': suffix,
                'url': url,
                'compression': comp,
                'bit_width': COMP_BITS.get(comp, 8),
            })
            i += 5 + param_size
        elif tag == 0x06:
            break
        elif tag == 0x03 and param_size <= 16:
            i += 5 + param_size
        else:
            # Unknown — try to skip by param_size
            if param_size < 512:
                i += 5 + param_size
            else:
                break
    channels.sort(key=lambda c: c['index'])
    return channels

def parse_v2_tier_def(data: bytes) -> Dict[int, List[dict]]:
    """
    Parse a v2 compact numeric tier def.
    All tags use TLV format: tag(1) + param(4 LE) + data(param bytes), except:
      0x00 enable: tag(1) + value(4) + flag(1) = 6 bytes (param interpreted as value, +1 flag byte)
      0x01 tier:   tag(1) + size(4) + flag(1) + channels((size-1)/16 * 16 bytes)
      0x06 end:    tag(1) + param(4) + total(4) — terminates scan
    Preamble tags (0x07, 0x03, etc.) are skipped via generic TLV skip.
    Returns {flag_byte: [channels]}.
    """
    code_to_comp = {
        0x00: 'bool', 0x14: 'uint3', 0x0D: 'int30', 0x01: 'uint8_t',
        0x02: 'int8_t', 0x17: 'float_001', 0x0E: 'percent_1',
        0x04: 'uint16_t', 0x05: 'int16_t', 0x0F: 'float_6000_1',
        0x15: 'float_600_2', 0x16: 'brake_temp_1', 0x07: 'float',
        0x08: 'int32_t', 0x09: 'uint32_t', 0x0A: 'double',
    }
    tiers: Dict[int, List[dict]] = {}
    i = 0
    while i < len(data):
        if i + 5 > len(data):
            break
        tag = data[i]
        param = struct.unpack_from('<I', data, i + 1)[0]

        if tag == 0x01:  # tier def: tag(1) + size(4) + flag(1) + channels
            size = param
            if i + 5 + size > len(data):
                break
            flag = data[i + 5]
            channels = []
            j = i + 6
            end = i + 5 + size
            while j + 16 <= end:
                ch_idx = struct.unpack_from('<I', data, j)[0]
                comp_code = struct.unpack_from('<I', data, j + 4)[0]
                bit_width = struct.unpack_from('<I', data, j + 8)[0]
                comp = code_to_comp.get(comp_code, f'code_{comp_code:02x}')
                channels.append({
                    'index': ch_idx,
                    'name': f'ch{ch_idx}',
                    'compression': comp,
                    'bit_width': bit_width,
                })
                j += 16
            tiers[flag] = channels
            i += 5 + size
        else:
            # Generic TLV skip: tag(1) + param(4) + data(param bytes).
            # Handles: 0x00 enable (param=1, data=flag), 0x06 end (param=4, data=total),
            # and preamble tags (0x07, 0x03, etc.).
            # Do NOT break on 0x06 — the buffer may contain a probe batch followed
            # by the real tier def; breaking at the first 0x06 would miss it.
            skip = 5 + param
            if i + skip > len(data):
                break
            i += skip
    return tiers

# ── Chunk reassembler ────────────────────────────────────────────────────────

class ChunkBuffer:
    """Accumulate 7C:00 type=0x01 data chunks, strip CRC32 trailer, reassemble.

    Pithouse sends two chunk formats on 7c:00 data: (a) [payload][CRC32] where
    CRC covers the full payload (used for standalone small messages + the simhub
    plugin's tier def), and (b) [flag:1][payload][CRC32] where CRC covers the
    flag+payload and the flag byte is a chunk-level session marker that must be
    stripped before concatenation (used for pithouse's multi-chunk tier def
    uploads on session 0x01). CRC match decides which format applies."""
    def __init__(self):
        self._chunks: Dict[int, bytes] = {}

    def add(self, seq: int, raw: bytes):
        if len(raw) < 4:
            self._chunks[seq] = raw
            return
        crc_wire = int.from_bytes(raw[-4:], 'little')
        if zlib.crc32(raw[:-4]) == crc_wire:
            self._chunks[seq] = raw[:-4]
        elif len(raw) >= 5 and zlib.crc32(raw[1:-4]) == crc_wire:
            self._chunks[seq] = raw[1:-4]
        else:
            self._chunks[seq] = raw[:-4]

    def message(self) -> bytes:
        if not self._chunks:
            return b''
        return b''.join(self._chunks[k] for k in sorted(self._chunks))

    def clear(self):
        self._chunks.clear()

# ── Response builders ────────────────────────────────────────────────────────

def resp_session_ack(session: int, ack_seq: int = 0) -> bytes:
    """fc:00 ack (wheel → plugin): 7E 05 C3 71 FC 00 [session] [ack_lo] [ack_hi] [cksum]"""
    payload = bytes([0xFC, 0x00, session, ack_seq & 0xFF, (ack_seq >> 8) & 0xFF])
    return build_frame(GRP_WHEEL, DEV_WHEEL_RSP, payload)


def resp_device_session_open(session: int, port: int) -> bytes:
    """Device-initiated session open. Matches the wheel's observed format:
    7E 0A C3 71 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [cksum]

    The port field appears twice; both are the u16 LE port number. The `fd 02`
    trailer is constant across every device-initiated open we've captured."""
    payload = bytes([
        0x7C, 0x00, session, SESSION_TYPE_OPEN,
        port & 0xFF, (port >> 8) & 0xFF,
        port & 0xFF, (port >> 8) & 0xFF,
        0xFD, 0x02,
    ])
    return build_frame(GRP_WHEEL, DEV_WHEEL_RSP, payload)

def build_session_data_frame(session: int, seq: int, chunk: bytes) -> bytes:
    """Build a wheel→host 7c:00 type=0x01 session data frame carrying `chunk`."""
    payload = bytes([0x7C, 0x00, session, 0x01, seq & 0xFF, (seq >> 8) & 0xFF]) + chunk
    return build_frame(GRP_WHEEL, DEV_WHEEL_RSP, payload)


def encode_rpc_message(obj: dict) -> bytes:
    """Wrap a JSON RPC reply in the wire format PitHouse uses for session
    data: 9-byte header (0x00 flag, 4-byte LE compressed size, 4-byte LE
    decompressed size) + zlib deflate stream. Observed in captured requests
    like `{"completelyRemove()": <uuid>, "id": N}` on session 0x0a."""
    body = json.dumps(obj, separators=(',', ':')).encode('utf-8')
    comp = zlib.compress(body)
    hdr = b'\x00' + struct.pack('<I', len(comp)) + struct.pack('<I', len(body))
    return hdr + comp


class WheelFileSystem:
    """Models the wheel's on-device Linux filesystem paths PitHouse interacts
    with. Authoritative data source for session 0x04 directory listings and
    session 0x09 configJson state. Default state is EMPTY so PitHouse sees no
    dashboards stored on a fresh sim.

    Files are stored as raw bytes with md5 + mtime metadata. Directories are
    implicit (derived from file paths). Persisted to sim/logs/wheel_fs.json.

    Relevant paths PitHouse expects:
      /home/moza/resource/dashes/<name>/<name>.mzdash       — dashboard body
      /home/moza/resource/dashes/<name>/<name>.mzdash_v2_10_3_05.png  — preview
      /home/moza/resource/tile_server/<game>/...            — map tiles
    """

    def __init__(self, persist_path: Optional[Path] = None):
        import base64
        self._b64 = base64
        self._persist_path = persist_path
        self._files: Dict[str, dict] = {}  # abs-path → {bytes, md5, mtime}
        self._load()

    def _load(self) -> None:
        if not self._persist_path or not self._persist_path.exists():
            return
        try:
            data = json.loads(self._persist_path.read_text())
            for path, meta in (data or {}).items():
                self._files[path] = {
                    'bytes': self._b64.b64decode(meta['bytes']),
                    'md5': meta.get('md5', ''),
                    'mtime': meta.get('mtime', 0),
                    'create': meta.get('create', 0),
                }
        except Exception:
            pass

    def _save(self) -> None:
        if not self._persist_path:
            return
        try:
            self._persist_path.parent.mkdir(parents=True, exist_ok=True)
            out = {
                p: {
                    'bytes': self._b64.b64encode(m['bytes']).decode(),
                    'md5': m['md5'],
                    'mtime': m['mtime'],
                    'create': m['create'],
                }
                for p, m in self._files.items()
            }
            self._persist_path.write_text(json.dumps(out, indent=2))
        except Exception:
            pass

    def write_file(self, path: str, data: bytes) -> None:
        import hashlib
        now_ms = int(time.time() * 1000)
        prev = self._files.get(path)
        create = prev['create'] if prev else now_ms
        self._files[path] = {
            'bytes': bytes(data),
            'md5': hashlib.md5(data).hexdigest(),
            'mtime': now_ms,
            'create': create,
        }
        self._save()

    def read_file(self, path: str) -> Optional[bytes]:
        entry = self._files.get(path)
        return entry['bytes'] if entry else None

    def stat(self, path: str) -> Optional[dict]:
        entry = self._files.get(path)
        if not entry:
            return None
        return {
            'fileSize': len(entry['bytes']),
            'md5': entry['md5'],
            'mtime': entry['mtime'],
            'create': entry['create'],
        }

    def delete(self, path: str) -> int:
        """Delete path (and any descendants if path is a directory prefix).
        Returns number of entries removed."""
        norm = path.rstrip('/')
        matched = [p for p in self._files
                   if p == norm or p.startswith(norm + '/')]
        for p in matched:
            del self._files[p]
        if matched:
            self._save()
        return len(matched)

    def list_children(self, path: str) -> List[dict]:
        """Return session-04-style child entries directly under `path`.
        Children are derived by looking at every stored file whose parent
        is `path` (direct file) or under `path/<name>/...` (subdirectory).
        A subdirectory entry is synthesized (fileSize=0, md5=''). Files use
        their stat + empty children list."""
        norm = path.rstrip('/') or '/'
        prefix = norm.rstrip('/') + '/'
        seen_dirs: Dict[str, dict] = {}
        children: List[dict] = []
        for p, meta in self._files.items():
            if not p.startswith(prefix):
                continue
            rest = p[len(prefix):]
            if '/' in rest:
                # file lives in a subdir — synthesize dir entry
                dname = rest.split('/', 1)[0]
                if dname not in seen_dirs:
                    seen_dirs[dname] = {
                        'children': self.list_children(prefix + dname),
                        'createTime': meta['create'],
                        'fileSize': 0,
                        'md5': '',
                        'modifyTime': meta['mtime'],
                        'name': dname,
                    }
            else:
                children.append({
                    'children': [],
                    'createTime': meta['create'],
                    'fileSize': len(meta['bytes']),
                    'md5': meta['md5'],
                    'modifyTime': meta['mtime'],
                    'name': rest,
                })
        return list(seen_dirs.values()) + children

    def dashboards(self) -> List[dict]:
        """Walk /home/moza/resource/dashes/*/*.mzdash and return the 2025-11
        firmware enableManager.dashboards schema. Each directory under
        /home/moza/resource/dashes is treated as one dashboard whose title
        = dirName unless the .mzdash JSON body carries a `name` field."""
        root = '/home/moza/resource/dashes/'
        out: List[dict] = []
        dash_dirs: Dict[str, Dict[str, str]] = {}
        for p, meta in self._files.items():
            if not p.startswith(root):
                continue
            rest = p[len(root):]
            if '/' not in rest:
                continue
            dname = rest.split('/', 1)[0]
            entry = dash_dirs.setdefault(dname, {})
            if rest.endswith('.mzdash'):
                entry['mzdash_path'] = p
                entry['mzdash_md5'] = meta['md5']
                entry['mzdash_mtime'] = meta['mtime']
                entry['mzdash_create'] = meta['create']
                entry['mzdash_size'] = len(meta['bytes'])
                # Try to parse title from body.
                try:
                    body = meta['bytes']
                    doc = json.loads(body.decode('utf-8'))
                    if isinstance(doc, dict):
                        t = doc.get('name') or doc.get('title')
                        if isinstance(t, str) and t:
                            entry['title'] = t
                except Exception:
                    pass
            elif rest.endswith('.png'):
                entry.setdefault('previews', []).append(p)
        for dname, info in dash_dirs.items():
            if 'mzdash_path' not in info:
                continue
            mtime_ms = info['mzdash_mtime']
            iso = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(mtime_ms / 1000))
            out.append({
                'createTime': '',
                'dirName': dname,
                'hash': info['mzdash_md5'],
                'id': f'sim-{info["mzdash_md5"][:8]}-{dname}',
                'idealDeviceInfos': [{
                    'deviceId': 17,
                    'hardwareVersion': 'RS21-W08-HW SM-DU-V14',
                    'networkId': 1,
                    'productType': 'W17 Display',
                }],
                'lastModified': iso,
                'previewImageFilePaths': info.get('previews', []),
                'resouceImageFilePaths': [],
                'title': info.get('title', dname),
                '_mzdash_size': info['mzdash_size'],
            })
        return out

    def tree(self) -> dict:
        """Full filesystem snapshot for debug inspection."""
        return {
            p: {
                'size': len(m['bytes']),
                'md5': m['md5'],
                'mtime': m['mtime'],
            }
            for p, m in sorted(self._files.items())
        }


# Canonical dashboard library names that PitHouse offers to the wheel (new
# firmware pushes this list back in `configJson()` host→device replies). Must
# appear in configJson state too — wheel reports this same list in
# `configJsonList` so PitHouse UI matches. Set from latestcaps capture.
_CONFIGJSON_CANONICAL_LIST = [
    "Core", "Grids", "Mono", "Nebula", "Pulse",
    "Rally V1", "Rally V2", "Rally V3", "Rally V4", "Rally V5", "Rally V6",
]


def build_configjson_state(dashboards: List[dict], title_id: int = 1,
                           display_version: int = 11,
                           canonical_list: Optional[List[str]] = None) -> bytes:
    """Build the session 0x09 wheel→host configJson state JSON + chunk envelope.

    PitHouse reads this to populate the Dashboard Manager UI. Without it the
    UI shows an empty dashboard list even though uploads succeeded. Returned
    bytes are the FULL payload (flag + sizes + zlib stream) ready to be
    chunked across 7c:00 data frames on session 0x09.

    Schema from usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng
    device→host blob (2025-11 firmware):
      {TitleId, configJsonList, disableManager, displayVersion, enableManager}
    Note: `disableManager`/`enableManager` (not ...Manager) and `dashboards`
    (not `updateDashboards`) — the 2026-04 firmware used different names.

    Envelope format (confirmed by decoding blobs in that capture):
      [flag:1B=0x00][comp_size:u32 LE][uncomp_size:u32 LE][zlib stream]"""
    state = {
        "TitleId": title_id,
        "configJsonList": canonical_list if canonical_list is not None else _CONFIGJSON_CANONICAL_LIST,
        "disableManager": {
            "dashboards": [],
            "imageRefMap": {},
            "rootPath": "/home/moza/resource/dashes",
        },
        "displayVersion": display_version,
        "enableManager": {
            "dashboards": dashboards,
            "imageRefMap": {},
            "rootPath": "/home/moza/resource/dashes",
        },
    }
    uncompressed = json.dumps(state, separators=(',', ':')).encode('utf-8')
    compressed = zlib.compress(uncompressed)
    envelope = (bytes([0x00])
                + struct.pack('<I', len(compressed))
                + struct.pack('<I', len(uncompressed))
                + compressed)
    return envelope


def build_session04_dir_listing(children: Optional[List[dict]] = None) -> bytes:
    """Build session 0x04 device→host root directory listing. Real wheel sends
    this shortly after session 0x04 opens to tell PitHouse what files already
    exist under /home/root. Schema from latestcaps wheel-connect capture:
      {children:[{children, createTime, fileSize, md5, modifyTime, name}],
       createTime, fileSize, md5, modifyTime, name:"root"}

    Prepended with a 9-byte envelope header: first 9 bytes before zlib magic
    in the capture are `0a d5 00 00 00 00 00 00 14` — the last byte (0x14)
    varies per message but the leading `0a` + `d5 00 00 00` (comp size) +
    `00 00 00 14` (uncomp size BE?) pattern suggests a different wire layout
    than session 0x09. For safety we just prepend the same 9-byte
    [flag][comp_LE][uncomp_LE] envelope PitHouse is observed to accept."""
    if children is None:
        children = [{
            "children": [],
            "createTime": -28800000,
            "fileSize": 0,
            "md5": "d41d8cd98f00b204e9800998ecf8427e",
            "modifyTime": int(time.time() * 1000),
            "name": "temp",
        }]
    listing = {
        "children": children,
        "createTime": -28800000,
        "fileSize": 0,
        "md5": "",
        "modifyTime": int(time.time() * 1000),
        "name": "root",
    }
    uncompressed = json.dumps(listing, separators=(',', ':')).encode('utf-8')
    compressed = zlib.compress(uncompressed)
    envelope = (bytes([0x00])
                + struct.pack('<I', len(compressed))
                + struct.pack('<I', len(uncompressed))
                + compressed)
    return envelope


def chunk_session_payload(session: int, start_seq: int, payload: bytes,
                          chunk_size: int = 58) -> List[bytes]:
    """Split `payload` into per-chunk 7c:00 session-data frames with CRC32
    trailer, matching the real wheel's pacing (~58 bytes of net data per
    chunk). Returns the list of wire-ready frames."""
    frames = []
    seq = start_seq
    for off in range(0, len(payload), chunk_size):
        net = payload[off:off + chunk_size]
        crc = struct.pack('<I', zlib.crc32(net))
        chunk = net + crc
        frames.append(build_session_data_frame(session, seq, chunk))
        seq = (seq + 1) & 0xFFFF
    return frames


class UploadTracker:
    """Decode zlib-compressed blobs embedded in PitHouse's 7c:00 uploads.

    PitHouse wraps content in FF-prefixed sub-messages on session data chunks.
    We buffer all session-data chunks, scan for zlib magic (78 9c/78 da), and
    decompress each stream. Decoded JSON is parsed to detect uploaded dashboard
    metadata (name/hash/createTime) which the sim then echoes in configJson
    state responses so PitHouse shows the uploaded dashboard as active."""

    def __init__(self):
        self._bufs: Dict[int, bytearray] = {}
        self.decoded_blobs: List[dict] = []
        self.uploaded_dashboards: List[dict] = []
        # Parsed RPC calls seen on any session (session 0x0a is the main
        # JSON RPC channel but any session carrying `{method(): args, id}`
        # shape is captured here).
        self.rpc_log: List[dict] = []

    def feed(self, session: int, chunk: bytes) -> Optional[dict]:
        """Append `chunk` to session buffer and return any newly-decoded blob.
        Handles CRC-aware trailer strip so callers can pass raw chunk payload
        (including CRC32) without pre-processing — the per-chunk 4-byte CRC
        would otherwise interleave with content and corrupt UTF-16LE path
        decoding and zlib stream reassembly across chunks."""
        self._log_chunk(session, chunk)
        buf = self._bufs.setdefault(session, bytearray())
        if len(chunk) >= 4:
            crc_wire = int.from_bytes(chunk[-4:], 'little')
            if zlib.crc32(chunk[:-4]) == crc_wire:
                chunk = chunk[:-4]
            elif len(chunk) >= 5 and zlib.crc32(chunk[1:-4]) == crc_wire:
                chunk = chunk[1:-4]
        buf.extend(chunk)
        return self._scan(session, buf)

    def _log_chunk(self, session: int, chunk: bytes) -> None:
        """Append raw chunk bytes (before CRC strip) to per-session dump files.
        Raw log keeps every byte PitHouse sent, not only the decoded-blob
        candidates — useful when protocol doesn't match zlib magic."""
        try:
            out_dir = Path(__file__).parent / 'logs' / 'uploads'
            out_dir.mkdir(parents=True, exist_ok=True)
            with open(out_dir / f'sess{session:02x}_raw.bin', 'ab') as f:
                f.write(chunk)
            with open(out_dir / f'sess{session:02x}_frames.log', 'a') as f:
                f.write(f'{time.strftime("%H:%M:%S")} {len(chunk):4d} {chunk.hex(" ")}\n')
        except Exception:
            pass

    def _scan(self, session: int, buf: bytearray) -> Optional[dict]:
        """Look for a new zlib stream. Decompress. Parse JSON if possible."""
        import re
        # Track byte offset at which we already decoded streams so we don't
        # re-emit the same content on every call.
        already = {b['session_offset'] for b in self.decoded_blobs if b.get('session') == session}
        for m in re.finditer(rb'\x78[\x9c\xda]', bytes(buf)):
            off = m.start()
            if off in already:
                continue
            try:
                dobj = zlib.decompressobj()
                decomp = dobj.decompress(bytes(buf[off:])) + dobj.flush()
            except zlib.error:
                continue
            if not dobj.eof or not decomp:
                continue
            blob = {'session': session, 'session_offset': off, 'size': len(decomp),
                    'raw': decomp}
            # Try UTF-8 JSON
            try:
                blob['json'] = json.loads(decomp.decode('utf-8'))
            except (UnicodeDecodeError, json.JSONDecodeError):
                blob['json'] = None
            # Try UTF-16LE text (wheel channel catalog + state blobs use this)
            try:
                blob['utf16'] = decomp.decode('utf-16-le', errors='replace')
            except Exception:
                blob['utf16'] = None
            self.decoded_blobs.append(blob)
            self._dump_blob_to_disk(blob)
            self._extract_dashboard_metadata(blob)
            return blob
        return None

    def _dump_blob_to_disk(self, blob: dict) -> None:
        """Persist decoded blob to sim/logs/uploads/ for offline inspection."""
        try:
            out_dir = Path(__file__).parent / 'logs' / 'uploads'
            out_dir.mkdir(parents=True, exist_ok=True)
            stem = f"sess{blob['session']:02x}_off{blob['session_offset']:05x}_sz{blob['size']}"
            (out_dir / f'{stem}.bin').write_bytes(blob.get('raw', b''))
            if isinstance(blob.get('json'), (dict, list)):
                (out_dir / f'{stem}.json').write_text(
                    json.dumps(blob['json'], indent=2, ensure_ascii=False))
            elif blob.get('utf16'):
                (out_dir / f'{stem}.utf16.txt').write_text(
                    blob['utf16'], encoding='utf-8', errors='replace')
        except Exception:
            pass

    def _extract_dashboard_metadata(self, blob: dict) -> None:
        """If `blob` JSON looks like a mzdash file or a configJson state,
        capture dashboard name + hash. mzdash files don't have a top-level
        `name`; dashboard name is carried in the session 0x04 file transfer
        path (`/home/moza/resource/dashes/<name>/<name>.mzdash`), decoded by
        feed() from the same session's UTF-16LE chunks and attached below."""
        j = blob.get('json')
        # RPC-shape detection: dict with an `id` field + a key matching
        # `<name>()` pattern is a wheel-device JSON RPC. Log it so the sim
        # (and MCP tooling) can inspect what PitHouse is asking for.
        if isinstance(j, dict) and 'id' in j:
            import re as _re
            for k in j.keys():
                if isinstance(k, str) and _re.match(r'^[A-Za-z_][A-Za-z_0-9]*\(\)$', k):
                    self.rpc_log.append({
                        'session': blob['session'],
                        'method': k.rstrip('()'),
                        'arg': j[k],
                        'id': j.get('id'),
                        'raw': j,
                        'ts': time.time(),
                    })
                    break
        if isinstance(j, dict):
            if 'name' in j and isinstance(j.get('name'), str):
                self.uploaded_dashboards.append({
                    'name': j['name'],
                    'id': j.get('id') or j.get('uuid') or '',
                    'raw_bytes': blob['size'],
                })
                return
            # 2025-11 firmware mzdash schema: {map, root, version}.
            # Dashboard name/title sits inside root (structure TBD — try common keys).
            root = j.get('root')
            if isinstance(root, dict):
                name = (root.get('name') or root.get('title')
                        or root.get('dirName') or root.get('dashName'))
                if isinstance(name, str) and name:
                    self.uploaded_dashboards.append({
                        'name': name,
                        'id': root.get('id') or root.get('uuid') or '',
                        'hash': root.get('hash') or '',
                        'version': j.get('version'),
                        'raw_bytes': blob['size'],
                        'source': 'mzdash_root',
                    })
                    return
            # Support both old (2026-04 firmware: disabledManager/enabledManager
            # with updateDashboards) and new (2025-11 firmware: disableManager/
            # enableManager with dashboards) schemas.
            for mgr_key in ('enableManager', 'disableManager',
                            'enabledManager', 'disabledManager'):
                mgr = j.get(mgr_key, {}) or {}
                entries = mgr.get('dashboards') or mgr.get('updateDashboards') or []
                for dash in entries:
                    if isinstance(dash, dict):
                        self.uploaded_dashboards.append({
                            'name': dash.get('title') or dash.get('dirName') or '',
                            'id': dash.get('id') or '',
                            'hash': dash.get('hash') or '',
                            'source': 'state',
                        })
            return
        # Heuristic for session 0x04 file transfer: content looks like mzdash
        # JSON but might not parse (embedded 7e corruption). Use UTF-16LE path
        # extracted separately from session chunks.
        if blob.get('mzdash_name'):
            self.uploaded_dashboards.append({
                'name': blob['mzdash_name'],
                'size': blob['size'],
                'source': 'file_transfer',
            })

    def extract_mzdash_path(self, session: int) -> Optional[str]:
        """Scan session buffer for `/home/moza/resource/dashes/<name>/<name>.mzdash`
        (UTF-16LE). PitHouse embeds this path in session 0x04 file transfers —
        giving us the dashboard's dirName/title before we ever decompress the
        file body."""
        buf = self._bufs.get(session)
        if not buf:
            return None
        try:
            text = bytes(buf).decode('utf-16-le', errors='ignore')
        except Exception:
            return None
        import re as _re
        m = _re.search(r'/home/moza/resource/dashes/([^/]+)/\1\.mzdash', text)
        return m.group(1) if m else None


def dash_upload_reply_loop(sim, alive, write_lock, log_fh, writer):
    """Fire _DASH_UPLOAD_REPLY_CHUNKS on any session that goes idle after an FF
    sub-message. PitHouse uploads a .mzdash as FF-prefixed chunks; after upload
    it waits for the wheel to echo back its currently-stored mzdash. Replay the
    recorded response stream from dash-upload.pcapng — same wire format,
    accepted by PitHouse as a valid device identity + stored-dashboard reply.

    Gated by `sim.dash_reply_enabled` — default off. The canned replay lies
    about stored state; the sim's live virtual FS + configJson push is the
    new source of truth. Kept here for ad-hoc debugging of the recorded flow.
    Exits when `alive` clears."""
    while alive.is_set():
        time.sleep(0.05)
        if not getattr(sim, 'dash_reply_enabled', False):
            continue
        now = time.monotonic()
        for session, last_ts in list(sim._upload_last_ff_ts.items()):
            if session in sim._upload_replied:
                continue
            if (now - last_ts) * 1000 < _DASH_UPLOAD_REPLY_IDLE_MS:
                continue
            sim._upload_replied.add(session)
            seq = sim._upload_next_seq.get(session, 0)
            with write_lock:
                log_fh.write(f'{_ts()} -- [dash_reply  ] session=0x{session:02x} start_seq=0x{seq:04x} chunks={len(_DASH_UPLOAD_REPLY_CHUNKS)}\n')
            for chunk in _DASH_UPLOAD_REPLY_CHUNKS:
                if not alive.is_set():
                    return
                frame = build_session_data_frame(session, seq, chunk)
                with write_lock:
                    writer(frame, 'dash_reply')
                sim.proactive_sent += 1
                seq = (seq + 1) & 0xFFFF
                time.sleep(0.005)

def resp_wheel_model_ident(model: str = 'VGS') -> bytes:
    """SimHub-plugin-compatible model name response routed via group 0x43.

    Frame: 7E [N] C3 71 87 01 <model 16-byte null-padded> [cksum]
    The plugin's identity parser at TelemetrySender.cs reads the name from
    data[4..] after matching data[2]==0x87 && data[3]==0x01, and treats any
    non-empty name as "display sub-device present".
    """
    payload = bytes([DISPLAY_IDENTITY_CMD, DISPLAY_SUBDEV]) + _id_str(model)
    return build_frame(GRP_WHEEL, DEV_WHEEL_RSP, payload)

def swap_nibbles(b: int) -> int:
    return ((b & 0x0F) << 4) | ((b & 0xF0) >> 4)

# ── ResponseReplay: record-and-replay device responses from captures ────────

class ResponseReplay:
    """
    Loads one or more PCAPNG captures and builds a (group, device, payload) →
    response_frame lookup table by pairing host→device requests with the first
    device→host response whose group/device match the expected XOR/swap identity.

    Used to stand in for devices (base, pedals, shifter, …) that the sim has no
    stateful handler for. Captured responses are replayed verbatim so checksums
    remain valid. First-observed response wins — fine for stateless reads.
    """

    PAIRING_WINDOW_SEC = 0.25

    def __init__(self):
        self._table: Dict[Tuple[int, int, bytes], bytes] = {}
        self.sources: List[str] = []
        # Stats: (group, device) → entry count
        self._by_device: Dict[Tuple[int, int], int] = {}

    def load_pcapng(self, path: str) -> int:
        """Extract, pair, insert. Returns number of new entries added."""
        entries = extract_from_pcapng(path)
        added = 0
        n = len(entries)

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
                if rsp_ts - ts > self.PAIRING_WINDOW_SEC:
                    break
                if rsp_dir != 'device' or not verify(rsp_frame) or len(rsp_frame) < 4:
                    continue
                if (rsp_frame[2] == expected_rsp_group
                        and rsp_frame[3] == expected_rsp_device):
                    # For group 0x43 burst commands, validate that the response
                    # sub-command (payload[0]) matches the request (cmd | 0x80).
                    # Without this, burst sends all pair with the first response.
                    rsp_pl = frame_payload(rsp_frame)
                    if (req_group == 0x43
                            and len(req_pl) >= 1 and len(rsp_pl) >= 1
                            and rsp_pl[0] != (req_pl[0] | 0x80)):
                        continue
                    key = (req_group, req_device, bytes(req_pl))
                    if key not in self._table:
                        self._table[key] = bytes(rsp_frame)
                        self._by_device[(req_group, req_device)] = \
                            self._by_device.get((req_group, req_device), 0) + 1
                        added += 1
                    break

        if added:
            self.sources.append(path)
        return added

    def lookup(self, frame: bytes) -> Optional[bytes]:
        if len(frame) < 4:
            return None
        group, device = frame[2], frame[3]
        key = (group, device, bytes(frame_payload(frame)))
        return self._table.get(key)

    def __len__(self) -> int:
        return len(self._table)

    def device_summary(self) -> List[Tuple[int, int, int]]:
        """Return [(group, device, entry_count), ...] sorted by count descending."""
        return sorted(
            [(g, d, n) for (g, d), n in self._by_device.items()],
            key=lambda x: -x[2],
        )

# ── WheelSimulator ────────────────────────────────────────────────────────────

class WheelSimulator:
    """
    Protocol state machine: receives plugin frames, sends wheel responses,
    decodes telemetry once tier definition is known.
    """

    def __init__(self, db: Dict[str, dict], replay: Optional[ResponseReplay] = None,
                 device_catalog: Optional[Dict[int, List[bytes]]] = None,
                 plugin_probe_rsp: Optional[Dict] = None,
                 pithouse_id_rsp: Optional[Dict] = None,
                 display_model_name: str = ''):
        self._db = db
        self._replay = replay
        self._plugin_probe_rsp = plugin_probe_rsp if plugin_probe_rsp is not None else _PLUGIN_PROBE_RSP
        self._pithouse_id_rsp = pithouse_id_rsp if pithouse_id_rsp is not None else _PITHOUSE_ID_RSP
        self._display_model_name = display_model_name or _DISPLAY_MODEL_NAME
        self.mgmt_session = 0
        self.telem_session = 0
        self.sessions_opened = 0
        self._reconnect_detected = False
        self.tier_def_received = False
        self.display_detected = False
        self.tiers: Dict[int, List[dict]] = {}  # flag_byte → channels
        self.channels: List[dict] = []           # all channels merged (for display)
        self.values: Dict[str, float] = {}
        self.frames_total = 0
        self.frames_telem = 0
        self.replay_hits = 0
        # Dedup unhandled commands by (group, device, cmd-hex) → count.
        # Also track total count and most-recent for the live UI.
        self.unhandled_counts: Dict[Tuple[int, int, str], int] = {}
        self.unhandled_labels: Dict[Tuple[int, int, str], str] = {}
        self.unhandled_total = 0
        self.last_unhandled: Optional[Tuple[int, int, str]] = None
        self.last_unhandled_label: Optional[str] = None
        self.last_frame_hex = ''
        self.last_handler_tag = ''
        # Per-handler-tag frame counts + rolling log of recent (tag, hex) pairs.
        self.cat_counts: Dict[str, int] = {}
        self.recent_frames: Deque[Tuple[str, str]] = collections.deque(maxlen=2000)
        self._fps_count = 0
        self.fps = 0.0
        self._fps_ts = time.monotonic()
        self._start = time.monotonic()
        self._bufs: Dict[int, ChunkBuffer] = {}  # session → chunk buffer
        # Proactive device-initiated state
        self._device_catalog = device_catalog or {}
        # Runtime host-sent session-open seqs, captured from the session_open
        # payload so the proactive sender can shift replayed catalog frames to
        # align with PitHouse's port counter.
        self.session_open_seqs: Dict[int, int] = {}
        self.catalog_sent = False
        self.proactive_sent = 0
        self.emitter: Optional[ConsoleEmitter] = None
        # Dashboard upload tracking: per-session last FF-prefix chunk time + reply state.
        # When PitHouse uploads a .mzdash, it sends FF-prefixed sub-messages on a
        # session; the wheel must then echo its stored dashboard back or PitHouse
        # stalls. A background Timer spawned from handle() populates _pending_sends
        # with the recorded reply stream; handle() drains _pending_sends on every
        # call and appends to its normal response list.
        self._upload_last_ff_ts: Dict[int, float] = {}
        self._upload_replied: set = set()
        self._upload_next_seq: Dict[int, int] = {}
        self._upload_reply_timer = None  # type: Optional[threading.Timer]
        self._pending_sends: List[bytes] = []
        self._pending_lock = threading.Lock()
        # Gate the canned dash-reply replay. Real upload traffic arrives on
        # session 0x04 FF-prefixed chunks; firing the recorded "stored dash"
        # reply mid-upload tricks PitHouse into aborting the real transfer.
        # Default: off. Re-enable per-session only after a full upload parses
        # successfully via _parse_session04_upload.
        self.dash_reply_enabled = False
        # RPC replies: track which rpc_log entries we've already responded to
        # and per-session outbound seq counters for our reply frames.
        self._rpc_replied_index = 0
        self._rpc_seq: Dict[int, int] = {}
        # Decode PitHouse uploads inline. Exposes what was pushed (dashboards,
        # channel catalog, tile-map config) so the sim can echo matching state
        # back to PitHouse (dashboard list / active-selection confirmation).
        self._upload_tracker = UploadTracker()
        # Device-initiated sessions we've queued opens for. Real wheel opens
        # 0x04/0x06/0x08/0x09/0x0a after the host brings up 0x01/0x02 — PitHouse
        # waits for the device's session 0x09 open before asking for the
        # dashboard list, so without these the Dashboard Manager UI stays empty.
        self.device_opened_sessions: Dict[int, int] = {}  # session → port
        self._device_init_started = False
        # Virtual wheel filesystem — authoritative for all device state
        # PitHouse inspects (dashboards, tile server, presets). Empty by
        # default so a fresh sim reports no stored dashboards. Persisted
        # across restarts at sim/logs/wheel_fs.json.
        self.fs = WheelFileSystem(
            persist_path=Path(__file__).parent / 'logs' / 'wheel_fs.json')

    @property
    def stored_dashboards(self) -> List[dict]:
        """Derived from wheel filesystem. Never a cached list — always reads
        live from WheelFileSystem so mutations take effect immediately."""
        return self.fs.dashboards()

    def _record(self, tag: str, frame: bytes) -> None:
        """Central bookkeeping: bump per-tag count, update last-frame + recent log."""
        self.cat_counts[tag] = self.cat_counts.get(tag, 0) + 1
        hx = frame.hex(' ')
        self.last_frame_hex = hx
        self.last_handler_tag = tag
        self.recent_frames.appendleft((tag, hx))

    def _fire_device_init(self) -> None:
        """Queue device-initiated session opens (0x04/0x06/0x08/0x09/0x0a) and
        the initial configJson state push. Runs once, ~150ms after the host
        has opened its sessions. Frames accumulate in _pending_sends and get
        flushed piggybacked on the next handle() return path."""
        frames: List[bytes] = []
        for sess, port, _ in _DEVICE_SESSIONS:
            frames.append(resp_device_session_open(sess, port))
            self.device_opened_sessions[sess] = port
        # Initial configJson state push on session 0x09. 2025-11 firmware sends
        # one blob (single-entry, not the old 3-blob sequence); schema handled
        # by build_configjson_state(). Dashboards derived live from FS.
        state = build_configjson_state(self.fs.dashboards())
        frames.extend(chunk_session_payload(0x09, 0x0100, state))
        self._session09_next_seq = 0x0100 + max(1, (len(state) + 57) // 58)
        # Root filesystem listing on session 0x04 — PitHouse uses this to
        # enumerate what's already on the wheel before a fresh upload.
        # Content derived from virtual FS (empty default → empty root).
        dir_listing = build_session04_dir_listing(self.fs.list_children('/'))
        frames.extend(chunk_session_payload(0x04, 0x0100, dir_listing))
        self._session04_next_seq = 0x0100 + max(1, (len(dir_listing) + 57) // 58)
        with self._pending_lock:
            self._pending_sends.extend(frames)
        self.cat_counts['device_init'] = self.cat_counts.get('device_init', 0) + len(frames)
        if self.emitter:
            self.emitter.emit_event('device_init',
                sessions=sorted(f'0x{s:02x}' for s in self.device_opened_sessions),
                dashboards=len(self.stored_dashboards),
                frames=len(frames))

    def _fire_state_refresh(self) -> None:
        """Re-push configJson + session 0x04 dir listing after a FS mutation
        (upload, delete). PitHouse Dashboard Manager picks up the new state
        without a full reconnect."""
        state = build_configjson_state(self.fs.dashboards())
        seq09 = getattr(self, '_session09_next_seq', 0x0200)
        frames = chunk_session_payload(0x09, seq09, state)
        self._session09_next_seq = seq09 + max(1, (len(state) + 57) // 58)
        dir_listing = build_session04_dir_listing(self.fs.list_children('/'))
        seq04 = getattr(self, '_session04_next_seq', 0x0200)
        frames.extend(chunk_session_payload(0x04, seq04, dir_listing))
        self._session04_next_seq = seq04 + max(1, (len(dir_listing) + 57) // 58)
        with self._pending_lock:
            self._pending_sends.extend(frames)
        self.cat_counts['state_refresh'] = self.cat_counts.get('state_refresh', 0) + len(frames)
        if self.emitter:
            self.emitter.emit_event('state_refresh',
                dashboards=len(self.fs.dashboards()), frames=len(frames))

    def _parse_session04_upload(self, session: int) -> Optional[dict]:
        """Called after a session 0x04 END marker. Reassembles chunks buffered
        by UploadTracker, extracts the UTF-16LE destination path + decompresses
        the zlib body, and registers the dashboard in `stored_dashboards`.
        Returns the new dashboard entry (or None if parse failed)."""
        name = self._upload_tracker.extract_mzdash_path(session)
        buf = bytes(self._upload_tracker._bufs.get(session, b''))
        if not buf:
            return None
        import re as _re
        zlib_match = _re.search(rb'\x78[\x9c\xda]', buf)
        if not zlib_match:
            return None
        try:
            decomp = zlib.decompress(buf[zlib_match.start():])
        except zlib.error:
            return None
        # mzdash is JSON but may have embedded 0x7e escapes that broke decoding
        # in older capture tools. Real mzdash files parse cleanly via UTF-8.
        try:
            mz = json.loads(decomp.decode('utf-8'))
        except (UnicodeDecodeError, json.JSONDecodeError):
            mz = None
        title = name or 'uploaded-dashboard'
        if isinstance(mz, dict):
            title = mz.get('name') or mz.get('title') or title
        # New-firmware schema (latestcaps capture): empty createTime, no
        # deletedDashboards, dashboards ride under enableManager.
        dirname = name or title
        # Write to virtual FS at canonical path. dashboards() derives entries
        # live from this, so configJson state reflects the new file on next
        # _fire_state_refresh().
        fs_path = f"/home/moza/resource/dashes/{dirname}/{dirname}.mzdash"
        self.fs.write_file(fs_path, decomp)
        # Clear buffer so a repeat upload doesn't double-emit.
        self._upload_tracker._bufs[session] = bytearray()
        # Return the synthesized dashboard entry (pulled fresh from FS).
        for d in self.fs.dashboards():
            if d['dirName'] == dirname:
                return d
        return None

    def _drain_rpc_log(self) -> None:
        """Walk new rpc_log entries since last drain and queue replies for each.
        Called after every session_data feed so replies ride out on the next
        handle() cycle."""
        entries = self._upload_tracker.rpc_log
        while self._rpc_replied_index < len(entries):
            entry = entries[self._rpc_replied_index]
            self._rpc_replied_index += 1
            try:
                self._handle_rpc(entry)
            except Exception as e:
                if self.emitter:
                    self.emitter.emit_event('rpc_err', method=entry.get('method'), err=str(e))

    def _handle_rpc(self, entry: dict) -> None:
        """Dispatch a parsed JSON RPC entry. Stateful handlers mutate
        stored_dashboards; unknown methods get a generic {id, result: true}
        reply so PitHouse's `id` callback fires. Reply is chunked onto the
        same session using the same 9-byte header + zlib stream wire format."""
        method = entry['method']
        arg = entry['arg']
        rpc_id = entry['id']
        session = entry['session']
        result: object = True
        # Known methods are dispatched here. Additions belong in P2/P4.
        if method == 'completelyRemove':
            removed = 0
            for d in list(self.stored_dashboards):
                if d.get('id') == arg or d.get('dirName') == arg:
                    removed += self.fs.delete(
                        f"/home/moza/resource/dashes/{d['dirName']}")
            result = {'removed': removed}
            if removed:
                self._fire_state_refresh()
        reply_obj = {'id': rpc_id, 'result': result}
        payload = encode_rpc_message(reply_obj)
        seq = self._rpc_seq.get(session, 0x0100)
        frames = chunk_session_payload(session, seq, payload)
        self._rpc_seq[session] = seq + max(1, (len(payload) + 57) // 58)
        with self._pending_lock:
            self._pending_sends.extend(frames)
        if self.emitter:
            self.emitter.emit_event('rpc_reply',
                method=method, id=rpc_id, session=f'0x{session:02x}',
                frames=len(frames))

    def _queue_dash_reply(self, session: int) -> None:
        """Build the 17 wheel→host session-data frames replaying the recorded
        dashboard upload reply stream on `session`, append to _pending_sends.
        Fires once per session (guarded by _upload_replied).

        Gated by `dash_reply_enabled`. Replay mid-upload causes PitHouse to
        skip its file transfer (it believes the dash is already stored). Only
        fire after a real upload parses — `_parse_session04_upload` flips the
        flag to True for that specific session."""
        if session in self._upload_replied:
            return
        if not self.dash_reply_enabled:
            return
        self._upload_replied.add(session)
        seq = self._upload_next_seq.get(session, 0)
        frames = []
        for chunk in _DASH_UPLOAD_REPLY_CHUNKS:
            frames.append(build_session_data_frame(session, seq, chunk))
            seq = (seq + 1) & 0xFFFF
        with self._pending_lock:
            self._pending_sends.extend(frames)
        self.cat_counts['dash_reply'] = self.cat_counts.get('dash_reply', 0) + len(frames)

    def handle(self, frame: bytes) -> List[bytes]:
        """Process one incoming frame + drain any timer-queued wheel→host sends.
        Dashboard-upload reply frames accumulate in _pending_sends via a background
        Timer; we flush them on the next handle() call so they ride out alongside
        the normal responses on the caller's write path. Pithouse's steady stream
        of heartbeats keeps handle() firing often enough for the reply to go out
        within a few ms of the timer firing."""
        rsp = self._handle_core(frame)
        if self._pending_sends:
            with self._pending_lock:
                extra = self._pending_sends
                self._pending_sends = []
            rsp = list(rsp) + extra
        return rsp

    def _handle_core(self, frame: bytes) -> List[bytes]:
        """Core frame processing (session/identity/replay/etc). handle() wraps
        this to also drain Timer-queued sends."""
        if not verify(frame) or len(frame) < 4:
            return []

        self.frames_total += 1
        group, device = frame[2], frame[3]

        # Stateful wheel handlers (session/tier def/display/telemetry). Returns
        # None if this isn't a known wheel-protocol command so we fall through.
        if group == GRP_HOST and device == DEV_WHEEL:
            result = self._handle_wheel(frame)
            if result is not None:
                tag, rsp = result
                self._record(tag, frame)
                return rsp

        # Plugin post-connect detection probes (wheel/base/hub/dash/pedals/handbrake
        # + wheel identity reads). Longest cmd-prefix match wins so specific
        # commands override generic group echoes.
        payload_all = frame_payload(frame)
        for _plen in (4, 3, 2, 1):
            if len(payload_all) >= _plen:
                _rsp = self._plugin_probe_rsp.get((group, device, bytes(payload_all[:_plen])))
                if _rsp is not None:
                    self._record('plugin_probe', frame)
                    return [build_frame(group | 0x80, swap_nibbles(device), _rsp)]

        # Plugin ProbeMozaDevice() base/hub probes — echo a framed ack so the
        # plugin's `first byte == 0x7E` check passes.
        synth = _PROBE_SYNTH.get((group, device))
        if synth is not None:
            rsp_group, rsp_dev = synth
            self._record('probe', frame)
            return [build_frame(rsp_group, rsp_dev, bytes(frame_payload(frame)))]

        # PitHouse VGS identity probes (groups 0x02/0x04/…/0x11, device=0x17).
        if device == DEV_WHEEL:
            key = (group, bytes(frame_payload(frame)))
            id_rsp = self._pithouse_id_rsp.get(key)
            if id_rsp is not None:
                self._record('identity', frame)
                return [build_frame(group | 0x80, DEV_WHEEL_RSP, id_rsp)]

        if group == 0x0E:
            self._record('fw_debug', frame)
            return []

        # Heartbeat (group 0x00, empty payload) — ACK only for devices the sim
        # can answer identity probes for. ACKing phantom devices (0x14, 0x15,
        # 0x18-0x1E) causes PitHouse to endlessly probe their identity.
        if group == 0x00 and len(payload_all) == 0:
            if device in _SIMULATED_DEVICES:
                self._record('heartbeat', frame)
                return [build_frame(0x80, swap_nibbles(device), b'')]
            return []  # silent drop — device not present

        # Bare 0x43 connection-keepalive ping (n=1, payload=0x00) — only ACK
        # for simulated devices; stray keepalives to dash/wheel-21 silently drop.
        if group == GRP_HOST and len(payload_all) == 1 and payload_all[0] == 0x00:
            if device in _SIMULATED_DEVICES:
                self._record('keepalive_43', frame)
                return [build_frame(GRP_WHEEL, swap_nibbles(device), b'\x80')]
            return []

        # Wheel write ACKs (group 0x3F/0x3E to dev 0x17) — real wheel echoes
        # the full request payload for these cmd-prefixes. Keeps us from
        # relying on payload-keyed replay for commands whose data varies per
        # call (LED index, channel CC, brightness…).
        for _plen in (2, 1):
            if len(payload_all) >= _plen:
                if (group, device, bytes(payload_all[:_plen])) in _WHEEL_ECHO_PREFIXES:
                    self._record('wheel_write', frame)
                    return [build_frame(group | 0x80,
                                        swap_nibbles(device),
                                        bytes(payload_all))]

        # Base settings writes (group 0x29 to dev 0x13) — PitHouse pushes hub
        # config with variable payloads. Real hub echoes them.
        if group == 0x29 and device == 0x13:
            self._record('base_write', frame)
            return [build_frame(group | 0x80, swap_nibbles(device),
                                bytes(payload_all))]

        # Replay layer — stateless query/response pairs recorded from captures.
        if self._replay is not None:
            recorded = self._replay.lookup(frame)
            if recorded:
                self.replay_hits += 1
                self._record('replay', frame)
                return [recorded]

        # Wheel config reads (group 0x40 to dev 0x17) — fallback echo for
        # queries not in the replay table. Keeps PitHouse from stalling on
        # LED config reads whose exact payloads vary per session.
        if group == 0x40 and device == DEV_WHEEL:
            self._record('wheel_cfg_echo', frame)
            return [build_frame(0xC0, DEV_WHEEL_RSP, bytes(payload_all))]

        # Nothing handled it — record for the gap report with semantic label.
        payload = frame_payload(frame)
        label = annotate(group, device, payload)
        cmd = payload[:2].hex(' ') if len(payload) >= 2 else payload.hex(' ')
        key = (group, device, cmd)
        self.unhandled_counts[key] = self.unhandled_counts.get(key, 0) + 1
        self.unhandled_labels.setdefault(key, label)
        self.unhandled_total += 1
        self.last_unhandled = key
        self.last_unhandled_label = label
        self._record('unhandled', frame)
        if self.emitter and self.unhandled_counts[key] == 1:
            self.emitter.emit_frame('unhandled', frame.hex(' '),
                                    label=f'grp=0x{group:02X} dev=0x{device:02X} {label}')
        return []

    def _handle_wheel(self, frame: bytes) -> Optional[Tuple[str, List[bytes]]]:
        """Wheel-protocol handler. Returns (tag, responses) if the command is
        recognised; returns None if the command isn't a wheel command we know,
        so the caller can fall through to replay."""
        payload = frame_payload(frame)
        if len(payload) < 2:
            return None

        cmd1, cmd2 = payload[0], payload[1]
        responses: List[bytes] = []

        # ── 7C:00 session management ────────────────────────────────────────
        if cmd1 == 0x7C and cmd2 == 0x00 and len(payload) >= 4:
            session = payload[2]
            msg_type = payload[3]
            tag = 'session'

            if msg_type == SESSION_TYPE_OPEN:
                tag = 'session_open'
                self.sessions_opened += 1
                if self.sessions_opened == 1:
                    self.mgmt_session = session
                elif self.sessions_opened == 2:
                    self.telem_session = session
                open_seq = 0
                if len(payload) >= 8:
                    open_seq = payload[6] | (payload[7] << 8)
                    self.session_open_seqs.setdefault(session, open_seq)
                responses.append(resp_session_ack(session, open_seq))
                if self.emitter:
                    kv = {'sessions': self.sessions_opened}
                    if self.sessions_opened == 1:
                        kv['mgmt'] = f'0x{session:02X}'
                    elif self.sessions_opened >= 2:
                        kv['telem'] = f'0x{session:02X}'
                    self.emitter.emit_event('session_open', **kv)
                # Real wheel opens its own sessions (0x04/0x06/0x08/0x09/0x0a)
                # after the host brings up mgmt + telem. Defer via a Timer so
                # the ack frame for this open goes out first, then our opens +
                # configJson state ride the next handle() drain cycle.
                if self.sessions_opened >= 2 and not self._device_init_started:
                    self._device_init_started = True
                    t = threading.Timer(0.15, self._fire_device_init)
                    t.daemon = True
                    t.start()

            elif msg_type == SESSION_TYPE_DATA and len(payload) >= 6:
                tag = 'session_data'
                # PitHouse resumes old sessions after sim restart (no session_open).
                if self.sessions_opened == 0 and not self._reconnect_detected:
                    self._reconnect_detected = True
                    if self.emitter:
                        self.emitter.emit_event('reconnect')
                seq = payload[4] | (payload[5] << 8)
                chunk = bytes(payload[6:])
                if session not in self._bufs:
                    self._bufs[session] = ChunkBuffer()
                self._bufs[session].add(seq, chunk)
                responses.append(resp_session_ack(session, seq))
                # Feed every chunk through the upload tracker. Scans buffered
                # chunks for zlib streams, decompresses, and extracts dashboard
                # metadata from mzdash/configJson content.
                blob = self._upload_tracker.feed(session, chunk)
                if blob is not None and self.emitter:
                    self.emitter.emit_event('upload_decoded',
                        session=f'0x{session:02x}', size=blob['size'],
                        kind=('json' if blob.get('json') else
                              'utf16' if blob.get('utf16') else 'binary'))
                # Drain any newly-parsed RPC calls → dispatch + queue replies.
                self._drain_rpc_log()
                # Track FF-prefix chunks (dashboard upload sub-messages). After
                # _DASH_UPLOAD_REPLY_IDLE_MS of idle, a Timer fires _queue_dash_reply
                # which appends the recorded wheel→host reply stream to
                # _pending_sends. Each new FF chunk resets the timer.
                if chunk and chunk[0] == 0xFF and session not in self._upload_replied:
                    self._upload_last_ff_ts[session] = time.monotonic()
                    cur = self._upload_next_seq.get(session, 0)
                    self._upload_next_seq[session] = max(cur, seq + 1)
                    if self._upload_reply_timer is not None:
                        self._upload_reply_timer.cancel()
                    self._upload_reply_timer = threading.Timer(
                        _DASH_UPLOAD_REPLY_IDLE_MS / 1000.0,
                        self._queue_dash_reply, args=(session,))
                    self._upload_reply_timer.daemon = True
                    self._upload_reply_timer.start()

                # Scan ALL session buffers for tier def data on every chunk.
                # v0 messages start with 0xFF (channel URLs, single-tier).
                # v2 messages use 0x01 tier tags (numeric, multi-tier, may have preamble).
                for sess_key, buf in self._bufs.items():
                    msg = buf.message()
                    if len(msg) < 5:
                        continue
                    chs = parse_v0_tier_def(msg, self._db)
                    if chs:
                        self.tiers[self.telem_session] = chs
                        self.channels = chs
                        self.tier_def_received = True
                        if self.emitter:
                            names = ','.join(c['name'] for c in chs[:8])
                            self.emitter.emit_event('tier_def', channels=len(chs), names=names)
                    else:
                        v2_tiers = parse_v2_tier_def(msg)
                        if v2_tiers:
                            self.tiers.update(v2_tiers)
                            self.channels = [
                                ch for flag in sorted(v2_tiers) for ch in v2_tiers[flag]
                            ]
                            self.tier_def_received = True
                            if self.emitter:
                                names = ','.join(c['name'] for c in self.channels[:8])
                                self.emitter.emit_event('tier_def', channels=len(self.channels), names=names)

            elif msg_type == SESSION_TYPE_END and len(payload) >= 6:
                # End-marker (type=0x00). Real wheel acks with its own end marker
                # on same session. PitHouse sends this after the session 0x04 file
                # upload completes — without a matching end reply, it retries or
                # stalls. `payload[4:6]` carries the last seq the host processed.
                tag = 'session_end'
                ack_seq = payload[4] | (payload[5] << 8)
                end_payload = bytes([0x7C, 0x00, session, SESSION_TYPE_END,
                                     ack_seq & 0xFF, (ack_seq >> 8) & 0xFF])
                responses.append(build_frame(GRP_WHEEL, DEV_WHEEL_RSP, end_payload))
                if self.emitter:
                    self.emitter.emit_event('session_end',
                        session=f'0x{session:02x}', ack_seq=ack_seq)
                # If this was the file-transfer session, try to parse the
                # uploaded mzdash and re-push configJson state so PitHouse's
                # UI reflects the new dashboard.
                if session in self.device_opened_sessions and session == 0x04:
                    entry = self._parse_session04_upload(session)
                    if entry is not None:
                        self._fire_state_refresh()
            return tag, responses

        # ── Display probe: cmd=0x07, payload[1]=0x01 (sub-device index) ────
        # SimHub plugin uses this single probe to detect a display (see
        # TelemetrySender.cs SendDisplayProbe → 0x87 response handling).
        # Answer unconditionally for any model with a `display` block so the
        # plugin's detection works. The broader CSP sub-device identity table
        # (0x02/0x04/0x05/0x06/0x08/0x09/0x0f/0x10/0x11) is gated separately
        # via `has_display_subdev` — VGS does not expose those and answering
        # them made PitHouse probe a phantom CSP-style sub-device.
        if (cmd1 == DISPLAY_PROBE_CMD and len(payload) >= 2
                and payload[1] == DISPLAY_SUBDEV and self._display_model_name):
            self.display_detected = True
            responses.append(resp_wheel_model_ident(self._display_model_name))
            if self.emitter:
                self.emitter.emit_event('display_detected', model=self._display_model_name)
            return 'display_probe', responses

        # ── 7C:23/27/1E display commands (host→wheel) ──────────────────
        # One-way notifications — no response expected. Silent consume.
        # 7C:23 = dashboard activate, 7C:27 = page cycle config,
        # 7C:1E = display settings push (brightness/timeout/orientation — all models).
        if cmd1 == 0x7C and cmd2 in (0x1E, 0x23, 0x27):
            return 'display_cfg', responses

        # ── 7D:23 telemetry frame ─────────────────────────────────────────
        if cmd1 == 0x7D and cmd2 == 0x23 and len(payload) >= 10:
            self.frames_telem += 1
            flag = payload[6]
            telem_data = bytes(payload[8:])
            channels = self.tiers.get(flag, self.channels)
            if channels:
                try:
                    self.values.update(decode_telemetry(telem_data, channels))
                except Exception:
                    pass
            now = time.monotonic()
            self._fps_count += 1
            dt = now - self._fps_ts
            if dt >= 1.0:
                self.fps = self._fps_count / dt
                self._fps_count = 0
                self._fps_ts = now
            if self.emitter and self.values:
                self.emitter.emit_telem(self.values)
            return 'telemetry', responses

        if cmd1 == 0xFC and cmd2 == 0x00:
            return 'session_ack_in', responses

        return None  # unhandled — fall through to replay

    @property
    def uptime(self) -> float:
        return time.monotonic() - self._start

# ── Terminal display ─────────────────────────────────────────────────────────

def render(sim: WheelSimulator, port: str):
    lines = ['\033[2J\033[H']
    lines.append('=== MOZA Wheel Simulator ===')
    lines.append(f'Port: {port}   Uptime: {sim.uptime:.0f}s')
    lines.append('')

    sess_info = f'{sim.sessions_opened} opened'
    if sim.sessions_opened >= 2:
        sess_info += f'  mgmt=0x{sim.mgmt_session:02X}  telem=0x{sim.telem_session:02X}'
    lines.append(f'Sessions:       {sess_info}')
    lines.append(f'Display:        {"DETECTED" if sim.display_detected else "not detected"}')

    if sim.tier_def_received:
        lines.append(f'Tier def:       {len(sim.channels)} channels received')
        names = '  '.join(f'{c["name"]}({c["bit_width"]}b)' for c in sim.channels[:6])
        if len(sim.channels) > 6:
            names += f'  +{len(sim.channels)-6} more'
        lines.append(f'Channels:       {names}')
    else:
        lines.append('Tier def:       waiting...')

    if sim._replay is not None:
        lines.append(f'Replay:         {len(sim._replay)} entries  hits={sim.replay_hits}'
                     f'  unhandled={sim.unhandled_total} ({len(sim.unhandled_counts)} unique)')
        if sim.last_unhandled is not None:
            g, d, c = sim.last_unhandled
            label = sim.last_unhandled_label or ''
            lines.append(f'Last unhandled: [{label}] grp=0x{g:02X} dev=0x{d:02X} cmd={c}')
    else:
        if sim.unhandled_total:
            lines.append(f'Unhandled:      {sim.unhandled_total} total, '
                         f'{len(sim.unhandled_counts)} unique (no replay loaded)')

    lines.append('')
    lines.append('┌─ Live Values ──────────────────────────────────┐')
    if sim.values:
        for name, val in list(sim.values.items())[:12]:
            if isinstance(val, float) and val != val:
                vstr = 'N/A'
            elif isinstance(val, float):
                vstr = f'{val:.4g}'
            else:
                vstr = str(val)
            lines.append(f'│  {name:<22} {vstr:<22}  │')
    else:
        lines.append('│  (no telemetry yet)                            │')
    lines.append('└────────────────────────────────────────────────┘')
    lines.append('')
    # Per-category counters in a stable order; omit tags never seen.
    tag_order = [
        'session_open', 'session_data', 'session', 'display_probe', 'display_cfg',
        'telemetry', 'plugin_probe', 'probe', 'identity', 'heartbeat',
        'keepalive_43', 'wheel_write', 'replay', 'unhandled',
    ]
    short = {
        'session_open': 'sess_open', 'session_data': 'sess_data',
        'session': 'sess', 'display_probe': 'disp', 'display_cfg': 'dcfg',
        'telemetry': 'telem', 'plugin_probe': 'plug', 'probe': 'probe',
        'identity': 'ident', 'heartbeat': 'hb', 'keepalive_43': 'ka43',
        'wheel_write': 'wwr', 'replay': 'replay', 'unhandled': 'unh',
    }
    parts = [f'{short[t]}={sim.cat_counts[t]}' for t in tag_order if sim.cat_counts.get(t)]
    # Catch any tag we didn't enumerate above (defensive; cheap).
    for t, n in sim.cat_counts.items():
        if t not in short and n:
            parts.append(f'{t}={n}')
    if sim.proactive_sent:
        parts.append(f'proactive={sim.proactive_sent}')
    if sim.catalog_sent:
        parts.append('catalog=done')
    lines.append(f'Frames: total={sim.frames_total}  {"  ".join(parts)}  FPS={sim.fps:.1f}')

    if sim.recent_frames:
        lines.append('Recent:')
        for tag, hx in sim.recent_frames:
            lines.append(f'  [{tag:<13}] {hx[:72]}')

    print('\n'.join(lines), end='', flush=True)

# ── Console emitter (non-interactive output) ────────────────────────────────

class ConsoleEmitter:
    """Structured line-oriented output for --console / --json modes."""

    def __init__(self, json_mode: bool = False):
        self.json_mode = json_mode
        self._last_telem_ts = 0.0
        self._last_state_ts = 0.0

    def _format(self, line_type: str, tag: str, **kv) -> str:
        ts = _ts()
        if self.json_mode:
            obj = {'ts': ts, 'type': line_type, 'tag': tag}
            obj.update(kv)
            return json.dumps(obj)
        parts = ' '.join(f'{k}={v}' for k, v in kv.items())
        return f'{ts} {line_type:<7} {tag:<18} {parts}'

    def emit_event(self, tag: str, **kv):
        print(self._format('EVENT', tag, **kv), flush=True)

    def emit_telem(self, values: dict):
        now = time.monotonic()
        if now - self._last_telem_ts < 1.0:
            return
        self._last_telem_ts = now
        formatted = {}
        for k, v in values.items():
            if isinstance(v, float):
                formatted[k] = round(v, 4) if v == v else None
            else:
                formatted[k] = v
        if self.json_mode:
            obj = {'ts': _ts(), 'type': 'TELEM', 'tag': 'values'}
            obj.update(formatted)
            print(json.dumps(obj), flush=True)
        else:
            parts = ' '.join(f'{k}={v}' for k, v in formatted.items())
            print(f'{_ts()} TELEM   {"values":<18} {parts}', flush=True)

    def emit_frame(self, tag: str, frame_hex: str, label: str = ''):
        kv = {'hex': frame_hex[:72]}
        if label:
            kv['label'] = label
        print(self._format('FRAME', tag, **kv), flush=True)

    def emit_stats(self, sim):
        now = time.monotonic()
        if now - self._last_state_ts < 5.0:
            return
        self._last_state_ts = now
        kv = {
            'uptime': f'{sim.uptime:.0f}s',
            'sessions': sim.sessions_opened,
            'tier_def': sim.tier_def_received,
            'display': sim.display_detected,
            'total': sim.frames_total,
            'telem': sim.frames_telem,
            'unhandled': sim.unhandled_total,
            'fps': round(sim.fps, 1),
        }
        if sim.catalog_sent:
            kv['catalog'] = 'done'
        print(self._format('STATE', 'snapshot', **kv), flush=True)

# ── PCAPNG extraction (tshark) ───────────────────────────────────────────────

def extract_from_pcapng(path: str) -> List[Tuple[str, float, bytes]]:
    """
    Extract Moza frames from a USBPcap PCAPNG file using tshark.
    Returns [(direction, timestamp_sec, frame_bytes)] where direction is 'host' or 'device'
    and timestamp_sec is the capture-relative time of the containing USB packet.

    CDC serial data lives in the usbcom layer. USBPcap stores host→device bytes in
    'usbcom.data.out_payload' and device→host bytes in 'usbcom.data.in_payload';
    each row has exactly one of the two populated. 'usb.src == "host"' identifies
    host→device packets; otherwise the source is a device address like '1.1.2'.
    """
    try:
        r = subprocess.run(
            ['tshark', '-r', path,
             '-Y', 'usbcom',
             '-T', 'fields',
             '-e', 'frame.time_relative',
             '-e', 'usb.src',
             '-e', 'usbcom.data.out_payload',
             '-e', 'usbcom.data.in_payload',
             '-E', 'separator=\t'],
            capture_output=True, text=True, timeout=60,
        )
    except FileNotFoundError:
        print('[ERROR] tshark not found. Install wireshark/tshark.', file=sys.stderr)
        return []
    except subprocess.TimeoutExpired:
        print('[ERROR] tshark timed out.', file=sys.stderr)
        return []

    entries: List[Tuple[str, float, bytes]] = []
    for line in r.stdout.splitlines():
        parts = line.split('\t')
        if len(parts) < 4:
            continue
        out_hex = parts[2].strip()
        in_hex = parts[3].strip()
        hex_str = (out_hex or in_hex).replace(':', '').replace(' ', '')
        if not hex_str:
            continue
        try:
            ts = float(parts[0].strip())
        except ValueError:
            ts = 0.0
        direction = 'host' if parts[1].strip().lower() == 'host' else 'device'
        try:
            raw = bytes.fromhex(hex_str)
        except ValueError:
            continue
        for frame in parse_frames(raw):
            entries.append((direction, ts, frame))

    if not entries:
        print(f'[WARN] No usbcom frames found in {path}', file=sys.stderr)
    return entries


def extract_device_catalog(path: str) -> Dict[int, List[bytes]]:
    """Extract device-initiated 7c:00 session data frames from a pcapng capture.

    Returns {session_id: [frame, ...]} sorted by sequence number, deduplicated.
    Only includes type=0x01 (data) chunks from group=0xC3 device=0x71.
    """
    entries = extract_from_pcapng(path)
    by_session: Dict[int, Dict[int, bytes]] = {}

    for direction, _ts, frame in entries:
        if direction != 'device' or not verify(frame) or len(frame) < 4:
            continue
        if frame[2] != GRP_WHEEL or frame[3] != DEV_WHEEL_RSP:
            continue
        payload = frame_payload(frame)
        if len(payload) < 6 or payload[0] != 0x7C or payload[1] != 0x00:
            continue
        session = payload[2]
        msg_type = payload[3]
        if msg_type != SESSION_TYPE_DATA:
            continue
        seq = payload[4] | (payload[5] << 8)
        if session not in by_session:
            by_session[session] = {}
        if seq not in by_session[session]:
            by_session[session][seq] = bytes(frame)

    result: Dict[int, List[bytes]] = {}
    for sess_id in sorted(by_session):
        chunks = by_session[sess_id]
        result[sess_id] = [chunks[s] for s in sorted(chunks)]
    return result


def extract_catalog_open_seqs(path: str) -> Dict[int, int]:
    """Extract host-sent session-open seq bytes per session from a capture.

    PitHouse's session-open payload `7c 00 [sess] 81 [flag_lo] [flag_hi]
    [seq_lo] [seq_hi] fd 02` carries the starting seq as its port counter.
    The wheel's first wheel→host data chunk on that session always lands at
    `host_open_seq + 3` (observed across all real VGS captures). Returning
    the host_open_seq lets the replay sender shift chunk seqs at runtime when
    PitHouse picks a different port counter than the capture used."""
    entries = extract_from_pcapng(path)
    result: Dict[int, int] = {}
    for direction, _ts, frame in entries:
        if direction != 'host' or not verify(frame) or len(frame) < 4:
            continue
        payload = frame_payload(frame)
        if len(payload) < 8 or payload[0] != 0x7C or payload[1] != 0x00:
            continue
        if payload[3] != SESSION_TYPE_OPEN:
            continue
        session = payload[2]
        open_seq = payload[6] | (payload[7] << 8)
        if session not in result:
            result[session] = open_seq
    return result


def rewrite_session_frame_seq(frame: bytes, new_seq: int) -> bytes:
    """Return a copy of `frame` with the 2-byte wheel→host session seq (offset
    8/9 in payload) replaced and the checksum recomputed. Used to shift
    replayed catalog frames to match PitHouse's runtime port counter."""
    buf = bytearray(frame)
    # Frame layout: 7e [N] group device payload[N] cksum
    # Payload layout for 7c:00 data: cmd1 cmd2 sess type seq_lo seq_hi ...
    # Indices in full frame: 4=cmd1, 5=cmd2, 6=sess, 7=type, 8=seq_lo, 9=seq_hi.
    buf[8] = new_seq & 0xFF
    buf[9] = (new_seq >> 8) & 0xFF
    buf[-1] = checksum(bytes(buf[:-1]))
    return bytes(buf)


def _chunk_catalog_message(session: int, message: bytes, start_seq: int) -> List[bytes]:
    """Chunk a TLV message into 7C:00 session data frames (wheel→host).

    Each chunk gets a CRC-32 trailer. Max 54 net bytes per chunk (58 with CRC).
    Returns complete Moza wire frames ready to send.
    """
    MAX_NET = 54
    frames: List[bytes] = []
    offset = 0
    seq = start_seq

    while offset < len(message):
        chunk_size = min(len(message) - offset, MAX_NET)
        chunk = message[offset:offset + chunk_size]

        crc = zlib.crc32(chunk) & 0xFFFFFFFF
        payload = (bytes([0x7C, 0x00, session, SESSION_TYPE_DATA,
                          seq & 0xFF, (seq >> 8) & 0xFF])
                   + chunk + struct.pack('<I', crc))

        frames.append(build_frame(GRP_WHEEL, DEV_WHEEL_RSP, payload))
        offset += chunk_size
        seq += 1

    return frames


def _build_session2_message(channel_urls: List[str]) -> bytes:
    """Build session 2 channel catalog TLV from a sorted list of URLs."""
    urls = sorted(channel_urls)
    msg = bytearray()
    msg.append(0xFF)
    msg += bytes([0x03]) + struct.pack('<I', 4) + struct.pack('<I', 1)
    for idx, url in enumerate(urls, start=1):
        url_bytes = url.encode('ascii')
        msg += bytes([0x04]) + struct.pack('<I', 1 + len(url_bytes))
        msg.append(idx & 0xFF)
        msg += url_bytes
    msg += bytes([0x03]) + struct.pack('<I', 4) + struct.pack('<I', 2)
    msg += bytes([0x06]) + struct.pack('<I', 4) + struct.pack('<I', len(urls))
    return bytes(msg)


def build_device_catalog(model: dict, channel_urls: List[str]) -> Dict[int, List[bytes]]:
    """Build proactive device catalog from model profile + channel URLs.

    Layout is model-dependent (see `session_layout` on the wheel profile):
      - `'legacy'` (default, used by CSP): session 1 = device description TLV,
        session 2 = channel catalog.
      - `'vgs_combined'`: matches real-hardware VGS
        (connect-wheel-start-game.pcapng, ts 21.53–21.55). Session 1 carries a
        tiny seed payload only (`ff 00 00 00 ff` then the session-1 magic TLV);
        session 2 carries the device description followed by the channel
        catalog. Putting the display hw_id TLV on session 1 caused PitHouse's
        dashboard tab to only "partially" detect the VGS display.
    """
    catalog: Dict[int, List[bytes]] = {}
    s1_desc = model.get('session1_desc')
    layout = model.get('session_layout', 'legacy')

    if layout == 'vgs_combined':
        # Session 1: real VGS sends two tiny chunks at seq 4 and 5. The wire
        # bytes are data + CRC-32 trailer; data[0] at seq 4 is 1 byte (`ff`)
        # with CRC `00 00 00 ff`; data at seq 5 is the 9-byte TLV
        # `03 04 00 00 00 01 00 00 00` with its own CRC.
        s1_frames = _chunk_catalog_message(0x01, b'\xff', start_seq=4)
        s1_frames += _chunk_catalog_message(
            0x01, bytes.fromhex('030400000001000000'), start_seq=5)
        catalog[0x01] = s1_frames

        # Session 2: device description first (seq 5..), then channel catalog.
        # Real VGS splits the description into 5 TLV-aligned sub-messages
        # (26/5/2/9/2 bytes), each carried in its own session-data chunk with
        # its own CRC. Boundaries matter — stuffing the whole blob into one
        # chunk (the legacy behavior) yielded only "partial" display detection.
        s2_frames: List[bytes] = []
        seq = 5
        if s1_desc:
            tlv_sizes = model.get('session2_desc_chunks') or (26, 5, 2, 9, 2)
            offset = 0
            for size in tlv_sizes:
                part = bytes(s1_desc[offset:offset + size])
                if not part:
                    break
                s2_frames += _chunk_catalog_message(0x02, part, start_seq=seq)
                offset += size
                seq += 1
            # Any leftover bytes get a final chunk (keeps things safe if a
            # future profile's desc runs longer than the split spec).
            if offset < len(s1_desc):
                tail = bytes(s1_desc[offset:])
                s2_frames += _chunk_catalog_message(0x02, tail, start_seq=seq)
                seq += 1
        if channel_urls:
            s2_msg = _build_session2_message(channel_urls)
            s2_frames += _chunk_catalog_message(0x02, s2_msg, start_seq=seq)
        if s2_frames:
            catalog[0x02] = s2_frames
        return catalog

    # Legacy CSP-style layout.
    if s1_desc:
        catalog[0x01] = _chunk_catalog_message(0x01, s1_desc, start_seq=4)
    if channel_urls:
        s2_msg = _build_session2_message(channel_urls)
        catalog[0x02] = _chunk_catalog_message(0x02, s2_msg, start_seq=5)
    return catalog


# ── Text log parsing ─────────────────────────────────────────────────────────

def parse_txt_log(path: str) -> List[bytes]:
    """
    Parse .txt frame log files.
    Supports: '[HH:MM:SS.mmm] 7e XX ...' and bare 'XX XX XX ...' hex lines.
    """
    frames = []
    with open(path, errors='replace') as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            # Strip optional timestamp prefix [HH:MM:SS.mmm]
            if line.startswith('['):
                idx = line.find(']')
                if idx >= 0:
                    line = line[idx + 1:].strip()
            try:
                raw = bytes.fromhex(line.replace(' ', ''))
            except ValueError:
                continue
            if raw and raw[0] == MSG_START:
                frames.extend(parse_frames(raw))
    return frames

# ── --validate mode ──────────────────────────────────────────────────────────

def cmd_validate(paths: List[str], db: Dict[str, dict],
                 replay: Optional[ResponseReplay] = None) -> int:
    total = 0
    cksum_errors = 0
    session_opens = 0
    tier_chunks = 0
    display_probes = 0
    telem_7d23 = 0
    telem_decoded = 0
    decode_errors = 0
    sample_values: List[Tuple[int, dict]] = []  # (flag, values)
    flags_seen: Dict[int, int] = {}             # flag → count

    sim = WheelSimulator(db, replay)
    telem_frames: List[bytes] = []  # collect for second pass

    for path in paths:
        p = Path(path)
        if not p.exists():
            print(f'[WARN] Not found: {path}')
            continue

        if path.endswith('.pcapng') or path.endswith('.pcap'):
            frames_with_dir = [(d, f) for d, _ts, f in extract_from_pcapng(path)]
        else:
            raw_frames = parse_txt_log(path)
            frames_with_dir = [('host', f) for f in raw_frames]

        if not frames_with_dir:
            print(f'[WARN] No frames extracted from {path}')
            continue

        # Pass 1 — counts, checksum, tier-def state. Tier defs and telemetry can
        # interleave (PitHouse sends some telemetry on flag 0x00 before the full
        # tier def for higher flags is buffered), so collect telemetry frames now
        # and decode them in pass 2 once tier state is complete.
        for direction, frame in frames_with_dir:
            total += 1
            if not verify(frame):
                cksum_errors += 1
                continue

            group = frame[2]
            device = frame[3]
            payload = frame_payload(frame)
            if len(payload) < 2:
                continue
            cmd1, cmd2 = payload[0], payload[1]

            if group == GRP_HOST and device == DEV_WHEEL:
                if cmd1 == 0x7C and cmd2 == 0x00 and len(payload) >= 4:
                    if payload[3] == SESSION_TYPE_OPEN:
                        session_opens += 1
                    elif payload[3] == SESSION_TYPE_DATA:
                        tier_chunks += 1
                elif cmd1 == DISPLAY_PROBE_CMD:
                    display_probes += 1
                elif cmd1 == 0x7D and cmd2 == 0x23 and len(payload) >= 10:
                    telem_7d23 += 1
                    telem_frames.append(bytes(payload))

                sim.handle(frame)

    # Pass 2 — decode telemetry against final tier state
    for payload in telem_frames:
        flag = payload[6]
        flags_seen[flag] = flags_seen.get(flag, 0) + 1
        channels = sim.tiers.get(flag)
        if not channels:
            continue
        try:
            vals = decode_telemetry(bytes(payload[8:]), channels)
            telem_decoded += 1
            if not any(f == flag for f, _ in sample_values):
                sample_values.append((flag, vals))
        except Exception:
            decode_errors += 1

    print()
    print('=== Validation Results ===')
    print(f'Source files:      {", ".join(paths)}')
    print(f'Total frames:      {total}')
    ok = '✓' if cksum_errors == 0 else '✗'
    print(f'{ok} Checksum errors:  {cksum_errors}')
    print(f'  Session opens:   {session_opens}')
    print(f'  Tier def chunks: {tier_chunks}')
    print(f'  Display probes:  {display_probes}')
    print(f'  Telem 7D:23:     {telem_7d23}')
    print(f'  Decoded OK:      {telem_decoded}')
    if decode_errors:
        print(f'  Decode errors:   {decode_errors}')

    if sim.tiers:
        print(f'\nTier definitions ({len(sim.tiers)} tiers):')
        for flag in sorted(sim.tiers):
            chs = sim.tiers[flag]
            total_bits = sum(c['bit_width'] for c in chs)
            print(f'\n  Flag 0x{flag:02X}: {len(chs)} channels, {total_bits} bits = {(total_bits+7)//8} bytes')
            bit = 0
            for c in chs:
                end = bit + c['bit_width'] - 1
                print(f'    [{bit:3d}–{end:3d}]  {c["name"]:<22} {c["compression"]:<16} {c["bit_width"]}b')
                bit += c['bit_width']

    if flags_seen:
        flags_str = ', '.join(f'0x{f:02X}({n})' for f, n in sorted(flags_seen.items()))
        print(f'\nTelemetry flags observed: {flags_str}')

    if replay is not None or sim.unhandled_total:
        replay_n = len(replay) if replay is not None else 0
        print(f'\nReplay: {replay_n} entries  hits={sim.replay_hits}  '
              f'unhandled={sim.unhandled_total} ({len(sim.unhandled_counts)} unique)')

    if sample_values:
        print(f'\nSample decoded frames (first {len(sample_values)}):')
        for i, (flag, vals) in enumerate(sample_values):
            summary = ', '.join(
                f'{k}={v:.4g}' if not (isinstance(v, float) and v != v) else f'{k}=N/A'
                for k, v in list(vals.items())[:8]
            )
            print(f'  [{i+1}] flag=0x{flag:02X}  {summary}')

    print()
    # Checksum errors are typically USB packet boundary fragmentation in 7C:00
    # type=0x01 dashboard upload chunks — not a real protocol decode issue.
    if cksum_errors == 0:
        print('✓ All checksums valid — decoder consistent with capture')
    else:
        ratio = cksum_errors / total if total else 0
        print(f'⚠ {cksum_errors}/{total} checksum errors ({ratio*100:.1f}%) — likely USB chunked-upload fragmentation')

    # Exit success if we extracted frames and decoded telemetry; checksum errors
    # in chunked uploads don't indicate decoder failure.
    return 0 if (total > 0 and telem_decoded == telem_7d23 and decode_errors == 0) else 1

# ── --replay-handshake mode ──────────────────────────────────────────────────

def cmd_replay_handshake(path: str, db: Dict[str, dict],
                         replay: Optional[ResponseReplay] = None) -> int:
    print(f'Replaying handshake: {path}\n')

    if path.endswith('.pcapng') or path.endswith('.pcap'):
        entries = [(d, f) for d, _ts, f in extract_from_pcapng(path)]
    else:
        frames = parse_txt_log(path)
        entries = [('host', f) for f in frames]

    sim = WheelSimulator(db, replay)
    all_ok = True

    for direction, frame in entries:
        if direction != 'host' or not verify(frame) or len(frame) < 4:
            continue

        group = frame[2]
        device = frame[3]
        payload = frame_payload(frame)
        if len(payload) < 2:
            continue
        cmd1, cmd2 = payload[0], payload[1]

        responses = sim.handle(frame)

        # Log significant wheel-protocol events
        if group == GRP_HOST and device == DEV_WHEEL:
            if cmd1 == 0x7C and cmd2 == 0x00 and len(payload) >= 4:
                session = payload[2]
                msg_type = payload[3]
                if msg_type == SESSION_TYPE_OPEN:
                    rsp_hex = responses[0].hex(' ') if responses else '(no response)'
                    num = sim.sessions_opened
                    print(f'  ✓ Session open 0x{session:02X} (#{num}) → ack: {rsp_hex}')
            elif cmd1 == DISPLAY_PROBE_CMD and responses:
                sub = payload[1] if len(payload) >= 2 else 0
                rsp_hex = responses[0].hex(' ')
                print(f'  ✓ Display probe 0x{cmd1:02X} sub=0x{sub:02X} → identity: {rsp_hex}')

    print()
    print('── Summary ──────────────────────────────────────────────────────────')

    if sim.sessions_opened >= 1:
        print(f'  ✓ Management session 0x{sim.mgmt_session:02X} acked')
    else:
        print('  ✗ Management session: no session open received')
        all_ok = False

    if sim.sessions_opened >= 2:
        print(f'  ✓ Telemetry session  0x{sim.telem_session:02X} acked (FlagByte=0x{sim.telem_session:02X})')
    else:
        print('  ✗ Telemetry session: only one session open received')
        all_ok = False

    if sim.tier_def_received:
        print(f'  ✓ Tier def received: {len(sim.channels)} channels on session 0x{sim.telem_session:02X}')
    else:
        print('  ✗ Tier def not parsed (check session or v0 message format)')
        all_ok = False

    if sim.display_detected:
        ident = resp_wheel_model_ident('Display')
        print(f'  ✓ Display probe (0x{DISPLAY_PROBE_CMD:02X}) → identity: {ident.hex(" ")}')
    else:
        print(f'  ✗ No display probe (0x{DISPLAY_PROBE_CMD:02X}) received from plugin')
        all_ok = False

    print()
    unique_count = len(sim.unhandled_counts)
    if replay is not None:
        print(f'  Replay table: {len(replay)} entries  |  hits: {sim.replay_hits}  '
              f'|  unhandled: {sim.unhandled_total} ({unique_count} unique)')
    else:
        print(f'  Replay: disabled  |  unhandled: {sim.unhandled_total} '
              f'({unique_count} unique)')

    if sim.unhandled_counts:
        print('  Unhandled (label  |  group, device, cmd):')
        for (g, d, c), n in sorted(sim.unhandled_counts.items(), key=lambda x: -x[1])[:20]:
            label = sim.unhandled_labels.get((g, d, c), '')
            print(f'    [{label:<38}] grp=0x{g:02X} dev=0x{d:02X} cmd={c:<6}  ×{n}')
        if unique_count > 20:
            print(f'    ... +{unique_count - 20} more distinct unhandled keys')

    print()
    if all_ok:
        print(f'✓ Handshake complete — telemetry expected on flag 0x{sim.telem_session:02X}')
    else:
        print('✗ Handshake incomplete — see failures above')

    return 0 if all_ok else 1

# ── --replay-self-test mode ─────────────────────────────────────────────────

def cmd_replay_self_test(path: str, db: Dict[str, dict]) -> int:
    """Load a capture as responses, feed every host frame from the SAME capture
    through the sim, and verify that **every frame whose key is in the replay
    table gets a replay hit**. Orphan frames (write-only, probes to absent
    devices) legitimately have no response and are reported but don't fail.
    Catches pairing/extraction/lookup bugs in ResponseReplay."""
    print(f'Self-test: {path}\n')

    replay = ResponseReplay()
    added = replay.load_pcapng(path)
    print(f'Replay table built: {added} entries from {path}')

    if path.endswith('.pcapng') or path.endswith('.pcap'):
        entries = [(d, f) for d, _ts, f in extract_from_pcapng(path)]
    else:
        frames = parse_txt_log(path)
        entries = [('host', f) for f in frames]

    sim = WheelSimulator(db, replay)
    host_frames = 0
    handled_by_wheel = 0
    handled_by_replay = 0
    orphan = 0            # frame had no key in replay table AND no wheel handler — expected
    missed_replay = 0     # frame's key IS in replay table but didn't hit — BUG
    missed_keys: Dict[Tuple[int, int, str], int] = {}

    for direction, frame in entries:
        if direction != 'host' or not verify(frame) or len(frame) < 4:
            continue
        host_frames += 1

        prev_hits = sim.replay_hits
        prev_unhandled_total = sim.unhandled_total
        sim.handle(frame)

        group, device = frame[2], frame[3]
        payload = frame_payload(frame)
        key = (group, device, bytes(payload))
        in_table = key in replay._table  # noqa: SLF001 — self-test peeks inside

        if sim.replay_hits > prev_hits:
            handled_by_replay += 1
        elif sim.unhandled_total > prev_unhandled_total:
            if in_table:
                # The table has a response for this key but the sim didn't hit it
                # → lookup bug. Record for investigation.
                missed_replay += 1
                cmd = payload[:2].hex(' ') if len(payload) >= 2 else ''
                missed_keys[(group, device, cmd)] = missed_keys.get((group, device, cmd), 0) + 1
            else:
                orphan += 1
        else:
            handled_by_wheel += 1

    print()
    print('── Self-test Results ──────────────────────────────────────────────')
    print(f'  Total host frames:    {host_frames}')
    print(f'  Handled by wheel:     {handled_by_wheel}')
    print(f'  Handled by replay:    {handled_by_replay}')
    print(f'  Orphan (no response expected): {orphan}')
    print(f'  Missed replay (BUG):  {missed_replay}')
    print()

    if missed_replay:
        print('  Missed replay keys (table has them, lookup didn\'t hit):')
        for (g, d, c), n in sorted(missed_keys.items(), key=lambda x: -x[1])[:10]:
            print(f'    grp=0x{g:02X} dev=0x{d:02X} cmd={c:<6}  ×{n}')

    ok = missed_replay == 0
    print()
    print('✓ Self-test passed' if ok else '✗ Self-test failed — see unhandled list')
    return 0 if ok else 1

# ── Live mode ─────────────────────────────────────────────────────────────────

def cmd_live(port: str, db: Dict[str, dict], replay: Optional[ResponseReplay] = None,
             device_catalog: Optional[Dict[int, List[bytes]]] = None,
             emits_7c23: bool = True,
             c7_23_frames: Optional[List[bytes]] = None,
             c7_23_reps: int = 13,
             catalog_capture_open_seqs: Optional[Dict[int, int]] = None,
             output_mode: str = 'interactive'):
    try:
        import serial
    except ImportError:
        print('[ERROR] pyserial is required for live mode.\n'
              '        Install with:  pip install pyserial',
              file=sys.stderr)
        sys.exit(1)

    print(f'Opening {port} ...')
    try:
        ser = serial.Serial(port, baudrate=115200, timeout=None)
    except (serial.SerialException, OSError) as e:
        print(f'[ERROR] Cannot open {port}: {e}')
        sys.exit(1)

    log_path = Path(__file__).parent / 'logs' / 'wheel_sim.log'
    log_fh = _open_session_log(log_path, port)
    print(f'[Logging to {log_path} (rotated last 5)]', file=sys.stderr)

    sim = WheelSimulator(db, replay, device_catalog)
    emitter = None
    if output_mode != 'interactive':
        emitter = ConsoleEmitter(json_mode=(output_mode == 'json'))
        sim.emitter = emitter
    alive = threading.Event()
    alive.set()
    write_lock = threading.Lock()

    def _write(frame: bytes, tag: str):
        ser.write(frame[:2])  # start + N unescaped
        for b in frame[2:]:
            ser.write(bytes([b]))
            if b == MSG_START:
                ser.write(b'\x7e')
        log_fh.write(f'{_ts()} TX [{tag:<13}] {frame.hex(" ")}\n')

    def read_loop():
        while alive.is_set():
            try:
                frame = read_one_frame(ser)
                if frame is None:
                    break
                sim.last_handler_tag = ''
                responses = sim.handle(frame)
                tag = sim.last_handler_tag or ('silent_drop' if not responses else 'unknown')
                if len(frame) >= 4:
                    label = annotate(frame[2], frame[3], frame_payload(frame))
                else:
                    label = ''
                with write_lock:
                    log_fh.write(f'{_ts()} RX [{tag:<13}] {frame.hex(" ")}  | {label}\n')
                    for rsp in responses:
                        _write(rsp, tag)
            except (OSError, serial.SerialException):
                break

    # Resolve which 7c:23 frame set to use; default preserves legacy behavior.
    frames_7c23 = c7_23_frames if c7_23_frames is not None else _7C_23_FRAMES

    def proactive_sender():
        time.sleep(0.3)

        # Phase 1: 7c:23 burst — tells PitHouse the wheel has dashboard pages.
        # Real CSP sends many copies of 2 page variants; real VGS sends each of
        # its 3 page variants once. We cycle each variant ~13× for stability.
        if emits_7c23 and frames_7c23:
            reps = max(1, c7_23_reps)
            total = reps * len(frames_7c23)
            with write_lock:
                log_fh.write(f'{_ts()} -- [proactive   ] 7c:23 burst start ({len(frames_7c23)} variants × {reps} = {total} frames)\n')
            for i in range(total):
                frame = frames_7c23[i % len(frames_7c23)]
                with write_lock:
                    _write(frame, 'proactive')
                sim.proactive_sent += 1
                time.sleep(0.0002)
        else:
            with write_lock:
                log_fh.write(f'{_ts()} -- [proactive   ] 7c:23 burst skipped (model does not emit)\n')

        # Phase 2: wait for session opens, then send channel catalog.
        catalog_sessions = sorted(sim._device_catalog.keys())
        if not catalog_sessions:
            return

        while alive.is_set() and sim.sessions_opened < 2 and not sim._reconnect_detected:
            time.sleep(0.05)
        if not alive.is_set():
            return

        time.sleep(0.05)
        for s in catalog_sessions:
            sim._bufs.pop(s, None)
        with write_lock:
            log_fh.write(f'{_ts()} -- [proactive   ] sending device catalog for sessions {catalog_sessions}\n')

        # Replay frames verbatim — wheel-direction seqs are independent of the
        # host's session-open seq. Matches pre-multi-model burst pacing.
        for sess_id in catalog_sessions:
            if sess_id not in (0x01, 0x02):
                continue
            for frame in sim._device_catalog[sess_id]:
                with write_lock:
                    _write(frame, 'catalog')
                sim.proactive_sent += 1
                time.sleep(0.001)

        sim.catalog_sent = True
        if sim.emitter:
            sim.emitter.emit_event('catalog_sent', frames=sim.proactive_sent)
        with write_lock:
            log_fh.write(f'{_ts()} -- [proactive   ] catalog complete, {sim.proactive_sent} frames sent\n')

        # Phase 3: periodic 7c:23 at ~1Hz (wheel does this continuously).
        if not (emits_7c23 and frames_7c23):
            return
        idx = 0
        while alive.is_set():
            frame = frames_7c23[idx % len(frames_7c23)]
            with write_lock:
                _write(frame, 'proactive')
            sim.proactive_sent += 1
            idx += 1
            time.sleep(1.0)

    t_read = threading.Thread(target=read_loop, daemon=True)
    t_proactive = threading.Thread(target=proactive_sender, daemon=True)
    t_dash_reply = threading.Thread(
        target=dash_upload_reply_loop,
        args=(sim, alive, write_lock, log_fh, _write), daemon=True)
    t_read.start()
    t_proactive.start()
    t_dash_reply.start()

    try:
        if output_mode == 'interactive':
            while alive.is_set():
                render(sim, port)
                time.sleep(0.1)
        else:
            while alive.is_set():
                if emitter:
                    emitter.emit_stats(sim)
                time.sleep(5.0)
    except KeyboardInterrupt:
        alive.clear()
        print('\n\n[Simulator stopped]', file=sys.stderr)
    finally:
        try:
            ser.close()
        except Exception:
            pass
        try:
            log_fh.close()
        except Exception:
            pass

# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description='MOZA Wheel Simulator',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument('port', nargs='?',
                        help='Serial port / pty path for live mode (e.g. /tmp/moza_wheel)')
    parser.add_argument('--validate', nargs='+', metavar='FILE',
                        help='Validate frames from PCAPNG or .txt log files')
    parser.add_argument('--replay-handshake', metavar='FILE',
                        help='Replay handshake from PCAPNG and verify simulator responses')
    parser.add_argument('--replay-responses', nargs='+', metavar='FILE', default=None,
                        help='Load PCAPNG capture(s) as the response table for non-wheel '
                             'commands (base, hub, pedals, etc.). First-observed response '
                             'wins; later captures only add new keys.')
    parser.add_argument('--replay-self-test', metavar='FILE',
                        help='Load a capture as responses, then feed every host frame from '
                             'that same capture through the sim and verify each gets a '
                             'response (via wheel handler or replay table).')
    parser.add_argument('--model', choices=sorted(WHEEL_MODELS.keys()), default='vgs',
                        help='Wheel model to simulate (default: vgs). '
                             'Available: ' + ', '.join(sorted(WHEEL_MODELS.keys())))
    parser.add_argument('--console', action='store_true',
                        help='Non-interactive console output (structured, grep-friendly)')
    parser.add_argument('--json', action='store_true',
                        help='NDJSON output (implies --console)')
    parser.add_argument('--mcp', action='store_true',
                        help='Run as MCP server (stdio transport) with embedded simulator')
    args = parser.parse_args()

    if args.json:
        args.console = True
    output_mode = 'json' if args.json else ('console' if args.console else 'interactive')
    if args.mcp:
        output_mode = 'mcp'

    # Populate global identity tables from selected model
    global _PLUGIN_PROBE_RSP, _PITHOUSE_ID_RSP, _DISPLAY_MODEL_NAME
    model = WHEEL_MODELS[args.model]
    _PLUGIN_PROBE_RSP, _PITHOUSE_ID_RSP = _build_identity_tables(model)
    _DISPLAY_MODEL_NAME = model.get('display', {}).get('name', '')
    print(f'[Model: {model["friendly"]} ({model["name"]}), Display: {_DISPLAY_MODEL_NAME}]',
          file=sys.stderr)

    db = load_telemetry_db()
    if db:
        print(f'[Loaded {len(db)} channels from Telemetry.json]', file=sys.stderr)

    # Default replay source: if the caller provided a live port but no explicit
    # --replay-responses, try the canonical VGS capture. Silently skip if missing.
    replay_paths = args.replay_responses
    if args.port and replay_paths is None:
        default = Path(__file__).parent.parent / 'usb-capture' / '12-04-26-2' / 'moza-startup-1.pcapng'
        if default.exists():
            replay_paths = [str(default)]

    replay: Optional[ResponseReplay] = None
    device_catalog: Optional[Dict[int, List[bytes]]] = None
    if replay_paths:
        replay = ResponseReplay()
        for p in replay_paths:
            added = replay.load_pcapng(p)
            print(f'[Replay: loaded {added} new entries from {p}]', file=sys.stderr)
        print(f'[Replay table: {len(replay)} unique (group, device, payload) keys]',
              file=sys.stderr)

    # Build device catalog — model can point at a pcapng to replay real-HW
    # session 1/2 frames verbatim. `build_device_catalog` synth was not
    # byte-identical for VGS (PitHouse stopped sending tier defs on session 1
    # after the abbreviated description), so VGS uses replay by default.
    channel_urls = [v['url'] for v in db.values()] if db else []
    device_catalog: Dict[int, List[bytes]] = {}
    catalog_capture_open_seqs: Dict[int, int] = {}
    catalog_source = model.get('catalog_pcapng')
    if catalog_source:
        cap_path = Path(__file__).parent.parent / catalog_source
        if cap_path.exists():
            raw = extract_device_catalog(str(cap_path))
            # Keep only sessions 1 and 2 — higher sessions are dashboard
            # upload / game-specific chunks that will mismatch at replay time.
            device_catalog = {s: frs for s, frs in raw.items() if s in (0x01, 0x02)}
            catalog_capture_open_seqs = extract_catalog_open_seqs(str(cap_path))
            print(f'[Device catalog: replay from {catalog_source} '
                  f'sessions {sorted(device_catalog.keys())} '
                  f'({sum(len(v) for v in device_catalog.values())} frames, '
                  f'baseline opens={ {s: catalog_capture_open_seqs.get(s) for s in sorted(device_catalog)} })]',
                  file=sys.stderr)
        else:
            print(f'[WARN] catalog_pcapng {cap_path} not found, falling back to synth',
                  file=sys.stderr)
    if not device_catalog:
        device_catalog = build_device_catalog(model, channel_urls)
        cat_total = sum(len(v) for v in device_catalog.values())
        if cat_total:
            print(f'[Device catalog: {cat_total} synth frames across sessions '
                  f'{sorted(device_catalog.keys())} ({len(channel_urls)} channels)]',
                  file=sys.stderr)

    if args.replay_self_test:
        sys.exit(cmd_replay_self_test(args.replay_self_test, db))
    if args.validate:
        sys.exit(cmd_validate(args.validate, db, replay))
    elif args.replay_handshake:
        sys.exit(cmd_replay_handshake(args.replay_handshake, db, replay))
    elif args.mcp:
        frames_name = model.get('_7c23_frames_name', 'CSP')
        frames_7c23 = {'CSP': _7C_23_FRAMES_CSP, 'VGS': _7C_23_FRAMES_VGS}.get(
            frames_name, _7C_23_FRAMES_CSP)
        import importlib.util
        _mcp_path = Path(__file__).parent / 'mcp_server.py'
        _spec = importlib.util.spec_from_file_location('mcp_server', _mcp_path)
        _mcp_mod = importlib.util.module_from_spec(_spec)
        _spec.loader.exec_module(_mcp_mod)
        _mcp_mod.configure(
            port=args.port or '',
            db=db, replay=replay, device_catalog=device_catalog,
            emits_7c23=bool(model.get('emits_7c23', True)),
            c7_23_frames=frames_7c23,
            c7_23_reps=int(model.get('_7c23_reps', 13)),
            catalog_capture_open_seqs=catalog_capture_open_seqs,
            model=model,
        )
        print('[MCP server starting — use sim_start to connect]', file=sys.stderr)
        _mcp_mod.run_stdio()
    elif args.port:
        frames_name = model.get('_7c23_frames_name', 'CSP')
        frames_7c23 = {'CSP': _7C_23_FRAMES_CSP, 'VGS': _7C_23_FRAMES_VGS}.get(
            frames_name, _7C_23_FRAMES_CSP)
        cmd_live(args.port, db, replay, device_catalog,
                 emits_7c23=bool(model.get('emits_7c23', True)),
                 c7_23_frames=frames_7c23,
                 c7_23_reps=int(model.get('_7c23_reps', 13)),
                 catalog_capture_open_seqs=catalog_capture_open_seqs,
                 output_mode=output_mode)
    else:
        parser.print_help()
        sys.exit(1)

if __name__ == '__main__':
    main()
