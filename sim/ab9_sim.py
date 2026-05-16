#!/usr/bin/env python3
"""
MOZA AB9 active-shifter simulator.

Emulates the MOZA AB9 (VID 0x346E PID 0x1000) CDC ACM pipe so the SimHub
plugin's AB9 manager (Devices/MozaAb9DeviceManager.cs) or PitHouse can
enumerate and configure the device against a host serial port.

Scope and non-scope:

  * Implemented — CDC ACM bus: identity probe cascade (groups 02/04/05/06/07/
    08/09/0F/10/11), stored-setting reads (group 0x1E, cmds 5D/A9/AF/B0/B2/D3/
    D4/D6/D7/D8), mode + slider writes (group 0x1F, cmds A9/AF/B0/B2/D3/D6),
    FFB-effect allocation + parameter pushes (group 0x20), heartbeat (group
    0x00). Responses match `docs/protocol/devices/ab9-shifter.md` and the byte
    sequences captured in `usb-capture/AB9/*.pcapng`.

  * Not implemented — HID interface (EP 0x83). Real AB9 streams 34-byte gear-
    state reports at ~1 kHz on this endpoint. The SimHub plugin never reads
    HID directly (SimHub consumes it via DirectInput at the OS level), so HID
    is unnecessary for plugin detection. PitHouse on Windows likewise reaches
    the device via CDC for configuration. Add a libcomposite f_hid function
    to the gadget if a future use case needs simulated gear events.

Setup guides:
  - sim/setup_ab9_gadget.sh — libcomposite + usbipd export for AB9 (mirrors
                              setup_usbip_gadget.sh but at PID 0x1000).
  - docs/protocol/devices/ab9-shifter.md — protocol reference.

Invocation:
    python3 sim/ab9_sim.py <port>                   # live mode
    python3 sim/ab9_sim.py --self-test              # offline byte-level checks
    python3 sim/ab9_sim.py --mcp <port>             # MCP stdio server
"""

from __future__ import annotations

import argparse
import collections
import json
import sys
import threading
import time
from pathlib import Path
from typing import Deque, Dict, List, Optional, Tuple

# Reuse framing primitives from wheel_sim. Same protocol; no need to copy.
sys.path.insert(0, str(Path(__file__).parent))
from wheel_sim import (  # type: ignore
    MSG_START,
    build_frame,
    frame_payload,
    read_one_frame,
    _open_session_log,
    _ts,
    _LOG_DIR,
)

# Recent-frames ring size for the MCP `ab9_recent` tool. Same magnitude as
# wheel_sim's _RECENT_FRAME_RING_SIZE — a few seconds of typical traffic on a
# 1 kHz poll cycle (D4/D7/D8 chained polls every ~25-35 ms = ~30 frames/s rx).
_RECENT_FRAME_RING_SIZE = 2000


# ── Protocol constants ──────────────────────────────────────────────────────

DEV_AB9 = 0x12       # host addresses AB9 main as device 0x12
DEV_AB9_RSP = 0x21   # AB9 responds with nibble-swapped 0x21

# Groups (host→AB9). Responses use group | 0x80.
GRP_HEARTBEAT = 0x00     # zero-payload heartbeat
GRP_ID_PRESENCE = 0x09   # presence probe (also AB9 port-claim probe)
GRP_ID_02 = 0x02         # identity slot 02
GRP_ID_04 = 0x04         # feature block 04
GRP_ID_05 = 0x05         # feature block 05
GRP_ID_06 = 0x06         # serial hw_id
GRP_ID_07 = 0x07         # device name
GRP_ID_08 = 0x08         # hardware revision string
GRP_ID_0F = 0x0F         # firmware/MCU string
GRP_ID_10 = 0x10         # per-device hash strings
GRP_ID_11 = 0x11         # identity-11 byte pair
GRP_READ = 0x1E          # PitHouse-style reads of stored settings (16-bit val)
GRP_WRITE = 0x1F         # mode + slider read/write (plugin uses for both)
GRP_FFB = 0x20           # FFB effect alloc / parameter pushes

# Default identity strings — verified against the Launch capture, with serial
# fields swapped for placeholder values.  Per-device hash strings (group 0x10
# slots 0/1) are real-looking but synthetic; PitHouse and the plugin only need
# them to be parseable, not to match a specific manufactured device.
DEFAULT_IDENTITY: Dict[str, object] = {
    'name': 'BA0 # MOT-1-V01',          # group 0x07:01 — model name
    'hw_version': 'AS23-BA0-HW BM-C',   # group 0x08:01
    'hw_sub': 'U-V15B',                 # group 0x08:02 — NUL-padded to 16
    'sw_version': 'AS23-BA0-MC BA0',    # group 0x0F:01
    'hash0': 'AB9SIM0000000000',        # group 0x10:00 — synthetic hash A
    'hash1': 'AB9SIM0000000001',        # group 0x10:01 — synthetic hash B
    'identity_11': bytes([0x04, 0x01]), # group 0x11:04
    'id_02': bytes([0x02]),             # group 0x02 — single status byte
    'id_04': bytes([0x01, 0x01, 0x0C, 0x02]),   # group 0x04:00 — feature block
    'id_05': bytes([0x01, 0x02, 0x28, 0x01]),   # group 0x05:00 — feature block
    'id_06': bytes.fromhex('4b00450001000000000020 20'.replace(' ', '')),
                                                # group 0x06 — 12B serial bytes
                                                # (capture had `4B 00 45 00 01 4E 56 44 4E 5A 20 20`;
                                                # mid-bytes are real-device-specific, we placeholder)
    'id_09': bytes([0x00, 0x08]),       # group 0x09 — presence/bus signature
}

# Default stored settings — read at boot from real AB9 in Launch capture.
DEFAULT_SETTINGS: Dict[int, int] = {
    0x5D: 0x0001,   # online/ready flag (1-byte payload on the wire, but stored as int)
    0xA9: 0x0064,   # Maximum Output Torque Limit (100)
    0xAF: 0x0064,   # Spring (100)
    0xB0: 0x0005,   # Natural Damping (5)
    0xB2: 0x0064,   # Natural Friction (100)
    0xD3: 0x0006,   # Shifter Mode (0x06 = 7+R Layout 1)
    0xD4: 0x0000,   # status word (always 0)
    0xD6: 0x0023,   # Gear Shift Mechanical Resistance (35)
    0xD7: 0x66E7,   # Shifter X analog (centre)
    0xD8: 0x8001,   # Shifter Y analog (centre)
}

# 0x5D returns a 1-byte payload (`7E 02 9E 21 5D 01 AA`); every other 0x1E
# read returns 2 value bytes. Track the wire size per cmd so replies match
# the capture verbatim.
_READ_VAL_SIZE: Dict[int, int] = {
    0x5D: 1,
    # All others below default to 2 bytes.
}


# ── Read / write helpers for plugin vs PitHouse semantics ───────────────────
# Group 0x1F is used by the SimHub plugin for BOTH reads and writes (it uses
# `BuildReadMessage` which emits a 3-byte-payload `<cmd_hi> <cmd_lo> <pad>`
# frame).  The wire-level distinction between a read (pad=0) and a write
# (value!=0) is ambiguous when the user actually writes value 0.
#
# Real-device behaviour on group 0x1F writes (per Launch / shifter-mode
# captures) is to respond with a 5-byte ack frame `7E 00 9F 21 4B` and
# update state — no value echo.  The plugin's response parser
# (`MozaResponseParser`) accepts the same group with a *value-bearing*
# payload `<cmd_hi> <cmd_lo> <value>` and uses any such reply to latch
# `_ab9Detected = true`.  An ack-only reply does NOT match the parser's
# command table and so wouldn't trigger detection.
#
# To support both the plugin AND PitHouse with one sim, we echo a full
# value frame on every group 0x1F transaction.  This is a strict superset of
# the ack-only response — both PitHouse and the plugin see something parseable.
# If we later observe PitHouse rejecting the longer reply we can switch on
# the value byte (==0 → echo, !=0 → ack), but no capture has shown that.

# Group 0x20 effect-allocation acks — verified against Launch capture phase 5.
# Shifter-mode byte → human-readable label, from PitHouse UI / capture
# decoding (`docs/protocol/devices/ab9-shifter.md`). Gaps 0x01..0x03 / 0x08
# were not exercised in any capture.
_MODE_LABELS: Dict[int, str] = {
    0x00: '5+R Layout 1',
    0x04: '6+R Layout 1',
    0x05: '6+R Layout 2',
    0x06: '7+R Layout 1',
    0x07: '7+R Layout 2',
    0x09: 'Sequential',
}

# Plugin slider name → group-0x1F cmd byte. Mirrors `MozaCommandDatabase.cs`
# (`ab9-mode`, `ab9-mech-resistance`, `ab9-spring`, `ab9-natural-damping`,
# `ab9-natural-friction`, `ab9-max-torque-limit`). Exposed for the MCP
# ab9_set_slider tool.
SLIDER_CMDS: Dict[str, int] = {
    'mode':             0xD3,
    'mech_resistance':  0xD6,
    'spring':           0xAF,
    'natural_damping':  0xB0,
    'natural_friction': 0xB2,
    'max_torque_limit': 0xA9,
}

_ACK_GRP20 = build_frame(0xA0, DEV_AB9_RSP, b'')     # `7E 00 A0 21 4C`
_ACK_GRP1F = build_frame(0x9F, DEV_AB9_RSP, b'')     # `7E 00 9F 21 4B` (kept for reference)
_ACK_HEARTBEAT = build_frame(0x80, DEV_AB9_RSP, b'')  # `7E 00 80 21 2C`


def _id_padded(s: str, n: int = 16) -> bytes:
    """Encode a string to ASCII, NUL-pad / truncate to exactly n bytes."""
    data = s.encode('ascii')[:n]
    return data + b'\x00' * (n - len(data))


def _id_field(slot: int, s: str, n: int = 16) -> bytes:
    """Identity field payload: 1-byte slot/index followed by n-byte padded string."""
    return bytes([slot]) + _id_padded(s, n)


# ── Ab9Simulator ────────────────────────────────────────────────────────────

class Ab9Simulator:
    """Stateful AB9 device emulator.

    Holds stored-setting state (mode + 5 slider values + analog X/Y), allocated
    FFB effect slots, and identity strings.  `handle(frame)` parses one
    incoming Moza frame and returns 0..N response frames to write.
    """

    def __init__(self, identity: Optional[Dict[str, object]] = None,
                 settings: Optional[Dict[int, int]] = None):
        self.identity = dict(DEFAULT_IDENTITY)
        if identity:
            self.identity.update(identity)
        self.settings: Dict[int, int] = dict(DEFAULT_SETTINGS)
        if settings:
            self.settings.update(settings)

        # FFB-effect allocation: monotonically increasing index returned for
        # each `7E 02 20 12 07 <type>` allocation request, mirroring the
        # firmware's `ffb.c:635 New Effect Index:N` behaviour. Reset to 0 on
        # disconnect — for the sim we just reset at construction.
        self.next_effect_index: int = 0
        self.effects: Dict[int, int] = {}   # index → type byte

        # Per-tag counters for diagnostics / MCP exposure.
        self.cat_counts: Dict[str, int] = {}

        # Last-handler tag picked up by the read loop for logging.
        self.last_handler_tag: str = ''

        # Diagnostics state — drives the MCP `ab9_*` query tools. Updated by
        # cmd_live / the MCP read loop on every handled frame.
        self.start_time: float = time.time()
        self.frames_rx: int = 0
        self.frames_tx: int = 0
        # Ring buffer of `(direction, tag, hex)` triples — direction is 'rx'
        # for host→AB9 (frames we received) or 'tx' for AB9→host responses.
        self.recent_frames: Deque[Tuple[str, str, str]] = collections.deque(
            maxlen=_RECENT_FRAME_RING_SIZE)
        # Frames whose (group, dev) the dispatcher dropped. Keyed by
        # (group, device, hex(payload[:4])) so we can spot unknown probes
        # before adding a handler.
        self.unhandled_counts: Dict[Tuple[int, int, str], int] = {}

    @property
    def uptime(self) -> float:
        return time.time() - self.start_time

    def snapshot(self) -> Dict[str, object]:
        """Compact serializable summary of stored device state — used by the
        MCP `ab9_status` / `ab9_settings` tools and by the offline self-test
        for debugging."""
        return {
            'uptime_s': round(self.uptime, 1),
            'frames_rx': self.frames_rx,
            'frames_tx': self.frames_tx,
            'mode': self.settings.get(0xD3),
            'mode_label': _MODE_LABELS.get(self.settings.get(0xD3, -1), 'unknown'),
            'sliders': {
                'mech_resistance': self.settings.get(0xD6),
                'spring':          self.settings.get(0xAF),
                'natural_damping': self.settings.get(0xB0),
                'natural_friction': self.settings.get(0xB2),
                'max_torque_limit': self.settings.get(0xA9),
            },
            'analog': {
                'shifter_x': self.settings.get(0xD7),
                'shifter_y': self.settings.get(0xD8),
            },
            'status_5d': self.settings.get(0x5D),
            'effects_allocated': self.next_effect_index,
            'effects': dict(self.effects),
            'unhandled_unique': len(self.unhandled_counts),
        }

    # ── Public entry point ──────────────────────────────────────────────

    def handle(self, frame: bytes) -> List[bytes]:
        """Dispatch one incoming frame; return the list of response frames."""
        if len(frame) < 4:
            self._tag('drop:short_frame')
            return []
        group = frame[2]
        dev = frame[3]
        payload = frame_payload(frame)

        if dev != DEV_AB9:
            # AB9 only answers writes targeted at its own DEV_AB9.  Real AB9
            # silently ignores anything else (e.g. wheelbase-targeted base
            # probes leak onto the AB9 USB pipe in early enumeration but the
            # device drops them).  This is load-bearing for plugin port
            # disambiguation: a 0xAB reply to `2B 13 02` would flag the port
            # as wheelbase territory and abort AB9 detection.
            self._tag(f'drop:dev_{dev:02x}')
            return []

        handler = self._dispatch.get(group)
        if handler is None:
            self._tag(f'drop:grp_{group:02x}')
            # Track unhandled (group, dev, payload-prefix) so the MCP
            # ab9_unhandled tool surfaces unknown probes for triage.
            key = (group, dev, payload[:4].hex())
            self.unhandled_counts[key] = self.unhandled_counts.get(key, 0) + 1
            return []
        return handler(self, payload)

    # ── Per-group handlers ──────────────────────────────────────────────

    def _h_heartbeat(self, payload: bytes) -> List[bytes]:
        self._tag('heartbeat')
        return [_ACK_HEARTBEAT]

    def _h_id_02(self, payload: bytes) -> List[bytes]:
        # Captured: host sends either `7E 00 02 12` (plugin: empty payload)
        # or `7E 01 02 12 00` (PitHouse: 1-byte 0x00).  Both must produce the
        # same reply.
        self._tag('id_02')
        return [build_frame(GRP_ID_02 | 0x80, DEV_AB9_RSP,
                            self.identity['id_02'])]  # type: ignore[arg-type]

    def _h_id_04(self, payload: bytes) -> List[bytes]:
        self._tag('id_04')
        return [build_frame(GRP_ID_04 | 0x80, DEV_AB9_RSP,
                            self.identity['id_04'])]  # type: ignore[arg-type]

    def _h_id_05(self, payload: bytes) -> List[bytes]:
        self._tag('id_05')
        return [build_frame(GRP_ID_05 | 0x80, DEV_AB9_RSP,
                            self.identity['id_05'])]  # type: ignore[arg-type]

    def _h_id_06(self, payload: bytes) -> List[bytes]:
        self._tag('id_06_serial')
        return [build_frame(GRP_ID_06 | 0x80, DEV_AB9_RSP,
                            self.identity['id_06'])]  # type: ignore[arg-type]

    def _h_id_07(self, payload: bytes) -> List[bytes]:
        slot = payload[0] if payload else 0x01
        self._tag('id_07_name')
        return [build_frame(GRP_ID_07 | 0x80, DEV_AB9_RSP,
                            _id_field(slot, self.identity['name']))]  # type: ignore[arg-type]

    def _h_id_08(self, payload: bytes) -> List[bytes]:
        slot = payload[0] if payload else 0x01
        if slot == 0x02:
            self._tag('id_08_hw_sub')
            return [build_frame(GRP_ID_08 | 0x80, DEV_AB9_RSP,
                                _id_field(slot, self.identity['hw_sub']))]  # type: ignore[arg-type]
        self._tag('id_08_hw')
        return [build_frame(GRP_ID_08 | 0x80, DEV_AB9_RSP,
                            _id_field(slot, self.identity['hw_version']))]  # type: ignore[arg-type]

    def _h_id_09(self, payload: bytes) -> List[bytes]:
        # The plugin's AB9 port-claim probe is `7E 00 09 12` with empty
        # payload; the response group bit-7 set (0x89) is the disambiguation
        # signal.  PitHouse also probes this group at boot.  Either way the
        # response is the static 2-byte presence signature.
        self._tag('id_09_presence')
        return [build_frame(GRP_ID_PRESENCE | 0x80, DEV_AB9_RSP,
                            self.identity['id_09'])]  # type: ignore[arg-type]

    def _h_id_0f(self, payload: bytes) -> List[bytes]:
        slot = payload[0] if payload else 0x01
        self._tag('id_0f_sw')
        return [build_frame(GRP_ID_0F | 0x80, DEV_AB9_RSP,
                            _id_field(slot, self.identity['sw_version']))]  # type: ignore[arg-type]

    def _h_id_10(self, payload: bytes) -> List[bytes]:
        slot = payload[0] if payload else 0x00
        key = 'hash1' if slot == 0x01 else 'hash0'
        self._tag(f'id_10_hash{slot}')
        return [build_frame(GRP_ID_10 | 0x80, DEV_AB9_RSP,
                            _id_field(slot, self.identity[key]))]  # type: ignore[arg-type]

    def _h_id_11(self, payload: bytes) -> List[bytes]:
        # Capture: payload is `04`, reply is `04 01`.  Match on slot byte and
        # send identity_11 with that slot prefix.
        slot = payload[0] if payload else 0x04
        self._tag('id_11')
        return [build_frame(GRP_ID_11 | 0x80, DEV_AB9_RSP,
                            bytes([slot]) + self.identity['identity_11'][1:])]  # type: ignore[index]

    def _h_read(self, payload: bytes) -> List[bytes]:
        """Group 0x1E: PitHouse stored-setting reads.

        Wire: `7E 01 1E 12 <cmd>` → reply `7E NN 9E 21 <cmd> <val_bytes...>`.
        Most cmds return a 16-bit value (big-endian on the wire); cmd 0x5D
        returns a single status byte.
        """
        if not payload:
            self._tag('drop:read_empty')
            return []
        cmd = payload[0]
        if cmd not in self.settings:
            self._tag(f'drop:read_{cmd:02x}')
            return []
        val = self.settings[cmd]
        size = _READ_VAL_SIZE.get(cmd, 2)
        if size == 1:
            val_bytes = bytes([val & 0xFF])
        else:
            val_bytes = bytes([(val >> 8) & 0xFF, val & 0xFF])
        self._tag(f'read_{cmd:02x}')
        return [build_frame(GRP_READ | 0x80, DEV_AB9_RSP,
                            bytes([cmd]) + val_bytes)]

    def _h_write(self, payload: bytes) -> List[bytes]:
        """Group 0x1F: mode + slider read/write (plugin path).

        Plugin frame: `7E 03 1F 12 <cmd_hi> <cmd_lo> <value>` where
        <cmd_hi> is the cmd byte (D3/D6/AF/B0/B2/A9) and <cmd_lo> is always
        0x00, <value> is 0..100 (or mode byte).  We always echo back a value
        frame so the plugin's response parser latches AB9 detection even on
        the first read sweep.
        """
        if len(payload) < 3:
            self._tag('drop:write_short')
            return []
        cmd_hi = payload[0]
        cmd_lo = payload[1]
        value = payload[2]

        # Heuristic: if cmd is a known slider/mode AND value is non-zero, it's
        # a write — update state.  Reads use value=0 (BuildReadMessage padding).
        # The plugin also occasionally sends a write of value 0 (e.g. Spring
        # slider physical floor is 10, but Damping can hit 0); we update state
        # in all cases to be safe, since the plugin's parser uses the response
        # as confirmation either way.
        if cmd_hi in (0xA9, 0xAF, 0xB0, 0xB2, 0xD3, 0xD6):
            # Persist the new value; for reads (value=0 with no actual change
            # intent) this is idempotent except when the user really did write
            # 0.  See note in module docstring.
            self.settings[cmd_hi] = value
            self._tag(f'write_{cmd_hi:02x}')
        else:
            self._tag(f'write_unknown_{cmd_hi:02x}')

        # Echo back in the format the plugin's parser accepts:
        #   `7E 03 9F 21 <cmd_hi> <cmd_lo> <value>` (3-byte payload)
        return [build_frame(GRP_WRITE | 0x80, DEV_AB9_RSP,
                            bytes([cmd_hi, cmd_lo, value]))]

    def _h_ffb(self, payload: bytes) -> List[bytes]:
        """Group 0x20: FFB effect allocation + parameter pushes.

        Sub-commands observed:
          cmd 0x07 <type>      — allocate effect, reply `7E 02 A0 21 07 <idx>`
          cmd 0x0E <v>         — init / channel count, reply ack
          cmd 0x13 <hi> <lo>   — commit / mask, reply ack
          cmd 0x0A 0x01 ...    — gear-shift vibration parameter push, reply ack
          cmd 0x0A 0x05 ...    — host-rendered engine-vibration refresh, reply ack
          cmd 0x0B 0x02/0x03   — engine-pulse ON/OFF pair, reply ack
          cmd 0x0D 0x01/02/03/05 — keepalive + RPM-track + sparse triggers, reply ack
          cmd 0x08 0x04/0x06   — signed-pair low-rate engine-cycle signal, reply ack
          (others)             — reply ack with generic ffb_sub_<hi>_<lo> tag
        """
        if not payload:
            self._tag('ffb_empty')
            return [_ACK_GRP20]
        sub = payload[0]
        if sub == 0x07 and len(payload) >= 2:
            effect_type = payload[1]
            self.next_effect_index += 1
            self.effects[self.next_effect_index] = effect_type
            self._tag('ffb_alloc')
            return [build_frame(GRP_FFB | 0x80, DEV_AB9_RSP,
                                bytes([0x07, self.next_effect_index & 0xFF]))]
        # Sub-cmds with a 2-byte (hi/lo) ID — tag each lo for diagnostics so the
        # MCP counters expose what the plugin is emitting.
        if sub in (0x0A, 0x0B, 0x0D, 0x08) and len(payload) >= 2:
            sub_lo = payload[1]
            self._tag(f'ffb_{sub:02x}_{sub_lo:02x}')
            return [_ACK_GRP20]
        if sub == 0x0E:
            self._tag('ffb_init')
            return [_ACK_GRP20]
        if sub == 0x13:
            self._tag('ffb_commit')
            return [_ACK_GRP20]
        self._tag(f'ffb_sub_{sub:02x}')
        return [_ACK_GRP20]

    # Group → handler.  Bound methods inside __init__ would create a per-
    # instance dict; this class-level dict using unbound functions does the
    # same job with less memory.
    _dispatch = {
        GRP_HEARTBEAT:   _h_heartbeat,
        GRP_ID_02:       _h_id_02,
        GRP_ID_04:       _h_id_04,
        GRP_ID_05:       _h_id_05,
        GRP_ID_06:       _h_id_06,
        GRP_ID_07:       _h_id_07,
        GRP_ID_08:       _h_id_08,
        GRP_ID_PRESENCE: _h_id_09,
        GRP_ID_0F:       _h_id_0f,
        GRP_ID_10:       _h_id_10,
        GRP_ID_11:       _h_id_11,
        GRP_READ:        _h_read,
        GRP_WRITE:       _h_write,
        GRP_FFB:         _h_ffb,
    }

    def _tag(self, tag: str) -> None:
        self.last_handler_tag = tag
        self.cat_counts[tag] = self.cat_counts.get(tag, 0) + 1


# ── Live mode ───────────────────────────────────────────────────────────────

def cmd_live(port: str) -> int:
    try:
        import serial  # type: ignore
    except ImportError:
        print('[ERROR] pyserial is required for live mode.\n'
              '        Install with: pip install pyserial',
              file=sys.stderr)
        return 1

    print(f'Opening {port} ...')
    try:
        ser = serial.Serial(port, baudrate=115200, timeout=None)
    except (serial.SerialException, OSError) as e:
        print(f'[ERROR] Cannot open {port}: {e}')
        return 1

    log_path = _LOG_DIR / 'ab9_sim.log'
    log_fh = _open_session_log(log_path, port)
    print(f'[Logging to {log_path} (rotated last 5)]', file=sys.stderr)
    log_fh.write(f'# ab9_sim (PID 0x1000 emulation) ready on {port}\n')

    sim = Ab9Simulator()
    alive = threading.Event()
    alive.set()
    write_lock = threading.Lock()

    def _write(frame: bytes, tag: str) -> None:
        # Same byte-stuffing approach as wheel_sim._write: stuff every 0x7E
        # in the body, single ser.write() call to avoid Wine/SerialPort
        # split-burst races.
        body = bytearray(frame[:2])
        for b in frame[2:]:
            body.append(b)
            if b == MSG_START:
                body.append(MSG_START)
        ser.write(bytes(body))
        log_fh.write(f'{_ts()} TX [{tag:<14}] {frame.hex(" ")}\n')
        sim.frames_tx += 1
        sim.recent_frames.append(('tx', tag, frame.hex()))

    def read_loop() -> None:
        while alive.is_set():
            try:
                frame = read_one_frame(ser)
                if frame is None:
                    time.sleep(0.05)
                    continue
                sim.last_handler_tag = ''
                responses = sim.handle(frame)
                tag = sim.last_handler_tag or ('silent_drop' if not responses else 'unknown')
                sim.frames_rx += 1
                sim.recent_frames.append(('rx', tag, frame.hex()))
                with write_lock:
                    log_fh.write(f'{_ts()} RX [{tag:<14}] {frame.hex(" ")}\n')
                    for rsp in responses:
                        _write(rsp, tag)
            except (OSError,) as e:
                log_fh.write(f'{_ts()} !! [io_error    ] {e}\n')
                time.sleep(0.5)
                continue
            except Exception as e:  # noqa: BLE001 — keep the read loop alive
                log_fh.write(f'{_ts()} !! [handler_err ] {type(e).__name__}: {e}\n')
                continue

    t = threading.Thread(target=read_loop, name='ab9_read_loop', daemon=True)
    t.start()

    print('[ab9_sim ready — Ctrl+C to stop]', file=sys.stderr)
    try:
        while alive.is_set():
            time.sleep(1.0)
    except KeyboardInterrupt:
        print('\n[stopping]', file=sys.stderr)
    finally:
        alive.clear()
        with write_lock:
            log_fh.write(f'# session end. cat_counts={json.dumps(sim.cat_counts)}\n')
            try:
                log_fh.close()
            except Exception:
                pass
        try:
            ser.close()
        except Exception:
            pass
    return 0


# ── Offline self-test ───────────────────────────────────────────────────────

def cmd_self_test() -> int:
    """Synthesize the captured handshake frames and verify the sim's responses
    match the byte sequences in `usb-capture/AB9/Launch and H-pattern gear
    engage.pcapng`.  No serial port required."""
    sim = Ab9Simulator()
    failures: List[str] = []

    def check(name: str, frame: bytes, expected_groups: List[int]) -> None:
        responses = sim.handle(frame)
        got_groups = [r[2] for r in responses]
        if got_groups != expected_groups:
            failures.append(
                f'{name}: expected response groups '
                f'{[f"0x{g:02x}" for g in expected_groups]}, '
                f'got {[f"0x{g:02x}" for g in got_groups]}')

    # Heartbeat
    check('heartbeat', build_frame(GRP_HEARTBEAT, DEV_AB9, b''), [0x80])

    # Presence probe — port-claim path
    check('presence', build_frame(GRP_ID_PRESENCE, DEV_AB9, b''), [0x89])

    # Identity cascade (capture order)
    check('id_04', build_frame(GRP_ID_04, DEV_AB9, b'\x00\x00\x00\x00'), [0x84])
    check('id_06', build_frame(GRP_ID_06, DEV_AB9, b''), [0x86])
    check('id_02_pithouse',
          build_frame(GRP_ID_02, DEV_AB9, b'\x00'), [0x82])
    check('id_02_plugin',
          build_frame(GRP_ID_02, DEV_AB9, b''), [0x82])
    check('id_05', build_frame(GRP_ID_05, DEV_AB9, b'\x00\x00\x00\x00'), [0x85])
    check('id_07', build_frame(GRP_ID_07, DEV_AB9, b'\x01'), [0x87])
    check('id_0f', build_frame(GRP_ID_0F, DEV_AB9, b'\x01'), [0x8F])
    check('id_11', build_frame(GRP_ID_11, DEV_AB9, b'\x04'), [0x91])
    check('id_08_hw', build_frame(GRP_ID_08, DEV_AB9, b'\x01'), [0x88])
    check('id_08_hw_sub', build_frame(GRP_ID_08, DEV_AB9, b'\x02'), [0x88])
    check('id_10_hash0', build_frame(GRP_ID_10, DEV_AB9, b'\x00'), [0x90])
    check('id_10_hash1', build_frame(GRP_ID_10, DEV_AB9, b'\x01'), [0x90])

    # Stored-setting reads (group 0x1E)
    for cmd in (0x5D, 0xA9, 0xAF, 0xB0, 0xB2, 0xD3, 0xD4, 0xD6, 0xD7, 0xD8):
        check(f'read_{cmd:02x}',
              build_frame(GRP_READ, DEV_AB9, bytes([cmd])), [0x9E])

    # Plugin-style writes (group 0x1F)
    for cmd in (0xA9, 0xAF, 0xB0, 0xB2, 0xD3, 0xD6):
        check(f'write_{cmd:02x}',
              build_frame(GRP_WRITE, DEV_AB9, bytes([cmd, 0x00, 0x32])), [0x9F])

    # FFB allocation: returns indices 1..6 on six successive 0x07 frames
    for i in range(6):
        responses = sim.handle(build_frame(GRP_FFB, DEV_AB9, bytes([0x07, 0x03])))
        if len(responses) != 1 or responses[0][2] != 0xA0:
            failures.append(f'ffb_alloc_{i}: wrong group {responses}')
        else:
            pl = frame_payload(responses[0])
            if pl != bytes([0x07, i + 1]):
                failures.append(f'ffb_alloc_{i}: expected payload 07 {i+1:02x}, got {pl.hex()}')

    # FFB params + commit
    check('ffb_init', build_frame(GRP_FFB, DEV_AB9, bytes([0x0E, 0x02])), [0xA0])
    check('ffb_commit', build_frame(GRP_FFB, DEV_AB9, b'\x13\x00\x00'), [0xA0])
    check('ffb_param',
          build_frame(GRP_FFB, DEV_AB9,
                      bytes.fromhex('0a010a3c000000000000000e006404000000')),
          [0xA0])

    # Cross-device leak: wheelbase-targeted base probe (dev 0x13) must drop
    no_resp = sim.handle(build_frame(0x2B, 0x13, b'\x02\x00\x00\x00'))
    if no_resp:
        failures.append(f'base_probe_leak: should drop, got {no_resp}')

    # Plugin write persists in state
    sim_b = Ab9Simulator()
    sim_b.handle(build_frame(GRP_WRITE, DEV_AB9, bytes([0xD3, 0x00, 0x09])))
    if sim_b.settings.get(0xD3) != 0x09:
        failures.append(f'write_persist: D3 should be 0x09, got 0x{sim_b.settings.get(0xD3):02x}')
    # Subsequent read via 0x1E returns the new mode
    rd = sim_b.handle(build_frame(GRP_READ, DEV_AB9, b'\xD3'))
    if not rd or frame_payload(rd[0]) != b'\xD3\x00\x09':
        failures.append(f'write_then_read: got {[r.hex() for r in rd]}')

    # Byte-for-byte comparison against captured real-AB9 frames (Launch capture
    # phase 0 + phase 4).  Verifies checksums, byte order, and default values.
    def byte_check(label: str, frame_in: bytes, expected_hex: str) -> None:
        sim_c = Ab9Simulator()
        responses = sim_c.handle(frame_in)
        if not responses:
            failures.append(f'{label}: no response')
            return
        got = responses[0].hex(' ')
        want = expected_hex.lower()
        if got != want:
            failures.append(f'{label}: sim={got}  cap={want}')

    byte_check('heartbeat_bytes',
               build_frame(GRP_HEARTBEAT, DEV_AB9, b''),
               '7e 00 80 21 2c')
    byte_check('presence_bytes',
               build_frame(GRP_ID_PRESENCE, DEV_AB9, b''),
               '7e 02 89 21 00 08 3f')
    # Capture phase 4 stored-setting reads (defaults seeded in DEFAULT_SETTINGS)
    for label, cmd, want in [
        ('read_5D_bytes', 0x5D, '7e 02 9e 21 5d 01 aa'),
        ('read_A9_bytes', 0xA9, '7e 03 9e 21 a9 00 64 5a'),
        ('read_AF_bytes', 0xAF, '7e 03 9e 21 af 00 64 60'),
        ('read_B0_bytes', 0xB0, '7e 03 9e 21 b0 00 05 02'),
        ('read_B2_bytes', 0xB2, '7e 03 9e 21 b2 00 64 63'),
        ('read_D3_bytes', 0xD3, '7e 03 9e 21 d3 00 06 26'),
        ('read_D4_bytes', 0xD4, '7e 03 9e 21 d4 00 00 21'),
        ('read_D6_bytes', 0xD6, '7e 03 9e 21 d6 00 23 46'),
        ('read_D7_bytes', 0xD7, '7e 03 9e 21 d7 66 e7 71'),
        ('read_D8_bytes', 0xD8, '7e 03 9e 21 d8 80 01 a6'),
    ]:
        byte_check(label, build_frame(GRP_READ, DEV_AB9, bytes([cmd])), want)

    # Snapshot shape — verify the MCP-facing summary has the keys callers
    # depend on.
    snap = sim_b.snapshot()
    for key in ('uptime_s', 'frames_rx', 'frames_tx', 'mode', 'mode_label',
                'sliders', 'analog', 'effects_allocated'):
        if key not in snap:
            failures.append(f'snapshot: missing key {key}')
    if snap.get('mode_label') != 'Sequential':
        failures.append(f'snapshot: mode_label expected "Sequential", got {snap.get("mode_label")!r}')
    for s in ('mech_resistance', 'spring', 'natural_damping',
              'natural_friction', 'max_torque_limit'):
        if s not in snap.get('sliders', {}):
            failures.append(f'snapshot: missing slider {s}')

    # Slider table — confirms ab9_set_slider's name→cmd lookup table covers
    # every command the plugin actually writes.
    for name, cmd in [
        ('mode', 0xD3), ('mech_resistance', 0xD6), ('spring', 0xAF),
        ('natural_damping', 0xB0), ('natural_friction', 0xB2),
        ('max_torque_limit', 0xA9),
    ]:
        if SLIDER_CMDS.get(name) != cmd:
            failures.append(f'SLIDER_CMDS[{name!r}] expected 0x{cmd:02X}, '
                            f'got 0x{SLIDER_CMDS.get(name, 0):02X}')

    if failures:
        print('SELF-TEST FAILURES:')
        for f in failures:
            print(f'  - {f}')
        return 1
    print('ab9_sim self-test: all checks passed')
    return 0


# ── Entry point ─────────────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(
        description='MOZA AB9 active-shifter simulator (CDC ACM, no HID)',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument('port', nargs='?',
                        help='Serial port (e.g. /dev/ttyGS1). Omit with --self-test.')
    parser.add_argument('--self-test', action='store_true',
                        help='Run offline handshake checks against captured byte sequences.')
    parser.add_argument('--mcp', action='store_true',
                        help='Run as MCP stdio server (lifecycle + state tools exposed to Claude Code). '
                             'Pass <port> as the default port for ab9_start.')
    args = parser.parse_args()

    if args.self_test:
        return cmd_self_test()
    if args.mcp:
        # Lazy import: only require the `mcp` SDK when actually running as a
        # server. --self-test and live mode must work on stripped envs.
        try:
            import ab9_mcp_server  # type: ignore
        except ImportError as e:
            print(f'[ERROR] MCP mode requires the mcp SDK: {e}\n'
                  '        Install with: pip install mcp',
                  file=sys.stderr)
            return 1
        if args.port:
            ab9_mcp_server.configure(port=args.port)
        ab9_mcp_server.run_stdio()
        return 0
    if not args.port:
        parser.error('positional <port> is required unless --self-test or --mcp is given')
    return cmd_live(args.port)


if __name__ == '__main__':
    sys.exit(main())
