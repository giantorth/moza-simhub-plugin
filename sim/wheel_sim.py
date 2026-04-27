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
import contextlib
import json
import shutil
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

# ── Timing constants ────────────────────────────────────────────────────────
# Hoisted from in-line literals so the cadences and their provenance live in
# one place. Where a value comes from a real-wheel capture, the source file is
# named in the comment.

# Session 0x09 keepalive cadence. Real wheel emits a zero-payload session 0x09
# chunk every ~2s after the initial state push (latestcaps post-state-push tail:
# 16 contiguous keepalives at 2s). PitHouse appears to require these to keep
# session 0x09 healthy.
_SESSION09_KEEPALIVE_SEC = 2.0

# Debounce for `_fire_state_refresh()`. Bursts of FS mutations (multi-file
# upload bundles writing 6 files in quick succession) would otherwise fire 6
# state pushes back-to-back and saturate the bus during PitHouse's own
# file-transfer ack flow.
_STATE_REFRESH_DEBOUNCE_SEC = 0.5

# Delay between `sessions_opened >= 2` and the device-init burst. Matches
# `_DEVICE_SESSIONS` first-entry delay; the timer is held on `self` so GC
# can't finalize it before fire.
_DEVICE_INIT_DELAY_SEC = 0.15

# Read loop backoffs.
_READ_LOOP_BACKOFF_EMPTY_SEC = 0.05    # peer closed pty / partial frame
_READ_LOOP_BACKOFF_ERROR_SEC = 0.5     # OSError / SerialException

# Proactive sender pacing.
_PROACTIVE_STARTUP_DELAY_SEC = 0.3            # initial settle before 7c:23 burst
_PROACTIVE_7C23_BURST_SPACING_SEC = 0.0002    # tight pacing for opening burst
_PROACTIVE_7C23_PERIOD_SEC = 1.0              # steady-state 7c:23 cadence (~1Hz)
_PROACTIVE_SESSION_OPEN_POLL_SEC = 0.05       # poll for sessions_opened>=2
_PROACTIVE_CATALOG_SETTLE_SEC = 0.05          # settle before catalog emit
_PROACTIVE_CATALOG_FRAME_SPACING_SEC = 0.001  # spacing between catalog frames

# Dash upload reply loop pacing.
_DASH_REPLY_POLL_SEC = 0.05            # outer idle poll for FF-trail timeout
_DASH_REPLY_FRAME_SPACING_SEC = 0.005  # per-chunk spacing in canned reply

# Live-mode main loop tick rates.
_RENDER_TICK_SEC = 0.1   # interactive UI redraw cadence
_STATS_TICK_SEC = 5.0    # structured stats emit cadence

# ── Buffer / size constants ─────────────────────────────────────────────────

# Recent-frames ring buffer for the live UI / MCP `sim_recent`. Sized to span
# a few seconds of typical traffic without unbounded growth.
_RECENT_FRAME_RING_SIZE = 2000

# Sanity bounds for the type-03 chunk size field embedded in upload buffers.
# Chunks outside this band are almost certainly false positives from the
# regex scan rather than real PitHouse upload chunks.
_UPLOAD_TYPE03_MIN_SIZE = 1000
_UPLOAD_TYPE03_MAX_SIZE = 10000

# Default LED counts when a model profile omits them. Real values come from
# `WHEEL_MODELS[<model>]['rpm_led_count' / 'button_led_count']`; these
# fallbacks match the most common (R9-style) wheel.
_DEFAULT_RPM_LED_COUNT = 10
_DEFAULT_BUTTON_LED_COUNT = 14

# ── Firmware-path / asset constants ─────────────────────────────────────────
# Path the real wheel firmware reports for stored dashboards. It is the
# *wheel's* filesystem (Linux ARM rootfs), NOT a host-side path. The sim
# echoes this prefix back inside session 0x09 state pushes and bundle entries
# so PitHouse parses the configJson identically to a real wheel.
_WHEEL_FW_DASH_ROOT = '/home/moza/resource/dashes'

# Factory configJson state files captured from real wheels. Bundled under
# `sim/` and loaded at startup (model dict picks one). See `sim/README.md`.
_FACTORY_STATE_FILE_W17_RGB = 'factory_state_w17_rgb.json'
_FACTORY_STATE_FILE_W08_SM = 'factory_state_w08_sm.json'
# KS Pro factory state captured byte-exact from
# `usb-capture/ksp/mozahubstartup.pcapng` session 0x0a state push (seq 11..69,
# 54B net per chunk, no CRC trailer). 10 factory dashboards (Rally V1..V6 +
# Core/Mono/Pulse/Grids — NO Nebula, vs the W17 RGB module's 11). Verified
# 2026-04-26 — pushing the W17 RGB factory list against KS Pro firmware left
# PitHouse refusing the state.
_FACTORY_STATE_FILE_KSPRO = 'factory_state_kspro.json'

# Host-side log directory. All sim runtime logs (wheel_fs.json, upload dumps,
# rpc_debug.log, configjson_versions.json, …) live under this path. Created
# on demand by callers via `mkdir(parents=True, exist_ok=True)`.
_LOG_DIR = Path(__file__).parent / 'logs'

# ── Swallowed-exception telemetry ───────────────────────────────────────────
# Counter for non-fatal exceptions caught at points where silent failure would
# otherwise mask real bugs. MCP `sim_counters` exposes this via
# `WheelSimulator.cat_counts['swallow:<where>']` (merged in `_swallow`).
_SWALLOWED_EXC_COUNTS: Dict[str, int] = {}
# When set to a `ConsoleEmitter`, every call to `_swallow` also emits a
# structured event. Live-mode wires this in `cmd_live`. Off by default so
# self-tests / offline decode runs stay quiet.
_SWALLOW_EMITTER: Optional['ConsoleEmitter'] = None


def _swallow(where: str, exc: BaseException, sim=None) -> None:
    """Record a caught-and-ignored exception. Use only at sites where silent
    failure would mask a real bug — pure cleanup paths (timer cancels,
    `finally` close, fallback decodes) should keep using bare `except: pass`.
    """
    _SWALLOWED_EXC_COUNTS[where] = _SWALLOWED_EXC_COUNTS.get(where, 0) + 1
    if sim is not None:
        key = f'swallow:{where}'
        sim.cat_counts[key] = sim.cat_counts.get(key, 0) + 1
    if _SWALLOW_EMITTER is not None:
        try:
            _SWALLOW_EMITTER.emit_event('swallowed_exc',
                where=where, exc_type=type(exc).__name__,
                msg=str(exc)[:120])
        except Exception:
            pass

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
    # KS Pro splits LED writes across 4 LED group pages (0x00..0x03). Pages
    # 0x02/0x03 only appear on KS Pro; CSP uses 0x00/0x01 only.
    (0x3F, 0x17, b'\x1f\x02'),  # per-LED color page 2 (KS Pro)
    (0x3F, 0x17, b'\x1f\x03'),  # per-LED color page 3 (KS Pro)
    (0x3F, 0x17, b'\x1e\x02'),  # channel CC enable page 2 (KS Pro)
    (0x3F, 0x17, b'\x1e\x03'),  # channel CC enable page 3 (KS Pro)
    (0x3F, 0x17, b'\x1d\x02'),  # page config 2 (KS Pro)
    (0x3F, 0x17, b'\x1d\x03'),  # page config 3 (KS Pro)
    (0x3F, 0x17, b'\x1c\x01'),  # page config (KS Pro)
    (0x3F, 0x17, b'\x1c\x02'),  # page config 2 (KS Pro)
    (0x3F, 0x17, b'\x1c\x03'),  # page config 3 (KS Pro)
    (0x3F, 0x17, b'\x1b\x02'),  # brightness page 2 (KS Pro)
    (0x3F, 0x17, b'\x1b\x03'),  # brightness page 3 (KS Pro)
    (0x3F, 0x17, b'\x27\x00'),  # LED display config page 0
    (0x3F, 0x17, b'\x27\x01'),  # LED display config page 1
    (0x3F, 0x17, b'\x27\x02'),  # LED display config page 2
    (0x3F, 0x17, b'\x27\x03'),  # LED display config page 3
    (0x3F, 0x17, b'\x27\x04'),  # LED display config page 4 (KS Pro)
    (0x3F, 0x17, b'\x2a\x00'),
    (0x3F, 0x17, b'\x2a\x01'),
    (0x3F, 0x17, b'\x2a\x02'),
    (0x3F, 0x17, b'\x2a\x03'),
    (0x3F, 0x17, b'\x2a\x04'),  # KS Pro extra page
    (0x3F, 0x17, b'\x0a\x00'),
    (0x3F, 0x17, b'\x24\xff'),  # display setting
    (0x3F, 0x17, b'\x20\x01'),
    (0x3F, 0x17, b'\x1a\x00'),  # RPM LED telemetry write
    (0x3F, 0x17, b'\x1a\x01'),  # button LED telemetry write
    (0x3F, 0x17, b'\x19\x00'),  # RPM LED color write
    (0x3F, 0x17, b'\x19\x01'),  # button LED color write
    (0x3E, 0x17, b'\x0b'),      # newer-wheel LED cmd (1-byte prefix)
    # 1-byte-prefix echoes observed in CSP captures (2026-04 firmware).
    # Real wheel echoes back full request payload for these config writes.
    # plugin_probe_rsp intercepts specific (cmd, sub) pairs first, so
    # 1-byte entries here only trigger for unmatched sub-bytes.
    (0x3F, 0x17, b'\x03'),      # misc config write
    (0x3F, 0x17, b'\x09'),      # config-mode probe (non-32 sub)
    (0x3F, 0x17, b'\x0a'),      # misc config write
    (0x3F, 0x17, b'\x0b'),      # LED cmd (0x3f variant)
    (0x3F, 0x17, b'\x21'),      # misc config write
}

# Hub main settings reads (group 0x1F dev 0x12). Real wheel response payload =
# `[sub_cmd_bytes_echoed, value_bytes]`. Values verified against real captures:
# `pithouse-switch-list-delete-upload-reupload.pcapng` (KS-Pro, post-config),
# `moza-startup.pcapng`, `putOnWheelAndOpenPitHouse.pcapng`. Match longest
# sub-cmd prefix first; the value blob is appended after the matched prefix
# verbatim.
#
# DB cross-refs are from `usb-capture/rs21_parameter.db` (CommandOperator
# table, MainComCtrl_* names). Cross-refs marked `?` are best-guess from
# adjacent entries; `unknown` means no DB hit at all.
#
# Wire shapes per sub-cmd (req → resp payload):
#   1B `0a`        → `0a 01`                         (MainComCtrl_GetCompatMode?)
#   1B `0f`        → `0f 00`                         (unknown — possibly status)
#   1B `10`        → `10 27 10`        v=0x2710=10000 (unknown — capacity?)
#   1B `18`        → `18 00 00`                      (unknown)
#   1B `19`        → `19 00 00`                      (unknown)
#   1B `20`        → `20 00 00`                      (unknown)
#   1B `21`        → `21 00 00`                      (unknown)
#   1B `23`        → `23 00 00 00 00`                (unknown — 4-byte zero)
#   1B `25`        → `25 27 10`        v=0x2710=10000 (unknown — capacity?)
#   1B `4d`        → `4d 00`                         (MainComCtrl_GetInterpolation)
#   2B `17 00`     → `17 00`                         (MainComCtrl_GetModeGameCompat)
#   2B `34 00`     → `34 00`                         (MainComCtrl_GetWorkMode?)
#   2B `36 00`     → `36 00`                         (MainComCtrl_GetDefaultFFBStatus?)
#   2B `46 00`     → `46 00`                         (MainComCtrl_GetBleMode)
#   2B `4c 00`     → `4c 00`                         (unknown — echo only)
#   3B `4e 0X ff`  → `4e 0X ff`                      (KS-Pro: per-port status echo)
#   2B `4e 0X`     → `4e 0X`                         (VGS variant; resp shape unconfirmed)
#   3B `4f 0X 00`  → `4f 0X ff 00`     (matches via 2B prefix `4f 0X`; per-port indexed read)
#   6B `55 00 ...` → `55 00 ` + _HUB_CALIB_SCALAR_55  (calibration constant)
#  14B `56 00 ...` → `56 00 ` + _HUB_CALIB_TRIPLE_56  (3 calibration constants)

# Hub calibration scalar (sub-cmd 0x55 00). 4 bytes captured from KS-Pro
# `pithouse-switch-list-delete-upload-reupload.pcapng` frame 61309. Decodes
# to 373.21 as a big-endian IEEE-754 float (4.866 if interpreted little-
# endian); the BE reading matches the "≈ 372.8" capture analysis in
# docs/wheel-sim-coverage-findings-2026-04-25.md, so the wheel likely emits
# this value in BE despite the 0x56 triple below being LE-encoded — possible
# fixed-point / firmware-quirk; sim replays the bytes verbatim either way.
_HUB_CALIB_SCALAR_55 = b'\x43\xba\x9b\x40'

# Hub calibration triple (sub-cmd 0x56 00). 12 bytes = three little-endian
# IEEE-754 floats: 8000.0, 4000.0, 10000.0. Likely encoder/torque/RPM
# scaling constants but no DB cross-ref. Note the inconsistency vs 0x55 —
# this sub-cmd uses LE float byte order, 0x55 appears to use BE.
_HUB_CALIB_TRIPLE_56 = b'\x00\x00\xfa\x45\x00\x00\x7a\x45\x00\x40\x1c\x46'

# Common sentinel value seen in 0x10 / 0x25 (BE u16 0x2710 = 10000). Likely
# a fixed capacity / scaling constant — not derived from runtime state.
_HUB_CFG_U16_10000 = b'\x27\x10'

# Base param-table write (group 0x2A dev 0x13) value-byte transformations.
# Verified constant across 5 captures (pithouse-switch + 3 moza-startup +
# putOnWheelAndOpenPitHouse). Real wheel returns these specific ack bytes
# for known sub-cmds; PitHouse may key on them for state validation.
#   43 01 [val]  → echo (active-dash slot commit; handled separately)
#   43 02 00     → 43 02 01
#   43 04 00     → 43 04 00 (echo)
#   44 01 00     → 44 01 80 (uninit sentinel)
_BASE_PARAM_ACK_VAL: Dict[bytes, int] = {
    b'\x43\x02': 0x01,
    b'\x44\x01': 0x80,
}

_HUB_CFG_VALUES: Dict[bytes, bytes] = {
    # 1-byte sub-cmds
    b'\x0a':         b'\x01',
    b'\x0f':         b'\x00',
    b'\x10':         _HUB_CFG_U16_10000,
    b'\x18':         b'\x00\x00',
    b'\x19':         b'\x00\x00',
    b'\x20':         b'\x00\x00',
    b'\x21':         b'\x00\x00',
    b'\x23':         b'\x00\x00\x00\x00',
    b'\x25':         _HUB_CFG_U16_10000,
    b'\x4d':         b'\x00',
    # 2-byte sub-cmds (echo only, no value byte)
    b'\x17\x00':     b'',
    b'\x34\x00':     b'',
    b'\x36\x00':     b'',
    b'\x46\x00':     b'',
    b'\x4c\x00':     b'',
    # 3-byte 0x4e (KS-Pro firmware)
    b'\x4e\x08\xff': b'',
    b'\x4e\x09\xff': b'',
    b'\x4e\x0a\xff': b'',
    b'\x4e\x0b\xff': b'',
    # 2-byte 0x4e (VGS firmware variant — response shape unconfirmed, echo)
    b'\x4e\x08':     b'',
    b'\x4e\x09':     b'',
    b'\x4e\x0a':     b'',
    b'\x4e\x0b':     b'',
    # 3-byte 0x4e with 0x80 selector (uninit sentinel observed on putOnWheel
    # capture). Echo the full sub-cmd including the 0x80 byte so PitHouse
    # sees the same shape it sent.
    b'\x4e\x08\x80': b'',
    b'\x4e\x09\x80': b'',
    b'\x4e\x0a\x80': b'',
    b'\x4e\x0b\x80': b'',
    # 0x4f indexed (matches via 2B prefix on 3B request)
    b'\x4f\x08':     b'\xff\x00',
    b'\x4f\x09':     b'\xff\x00',
    b'\x4f\x0a':     b'\xff\x00',
    b'\x4f\x0b':     b'\xff\x00',
    # 0x55 calibration scalar — see _HUB_CALIB_SCALAR_55 above
    b'\x55\x00':     _HUB_CALIB_SCALAR_55,
    # 0x56 calibration triple — see _HUB_CALIB_TRIPLE_56 above
    b'\x56\x00':     _HUB_CALIB_TRIPLE_56,
}

def _id_str(s: str) -> bytes:
    """16-byte null-padded ASCII identity string."""
    return s.encode('ascii')[:16].ljust(16, b'\x00')

def _id_slices(s: str, n: int = 4) -> List[bytes]:
    """Split identity string into n 16-byte null-padded slices.

    PitHouse queries longer identity strings via sequential sub-byte probes
    (07:01, 07:02, …). Each slice answers one probe; unused trailing slices
    are all-null, which PitHouse simply ignores if it stops at 01.
    """
    data = s.encode('ascii')[:n * 16].ljust(n * 16, b'\x00')
    return [data[i * 16:(i + 1) * 16] for i in range(n)]

# ── Wheel model profiles ──────────────────────────────────────────────────
# Each profile defines the identity strings and protocol details for a
# specific wheel model. Selected via --model CLI arg (default: vgs).

WHEEL_MODELS: Dict[str, dict] = {
    'vgs': {
        'name': 'VGS',
        'friendly': 'Vision GS',
        'rpm_led_count': 10,
        'button_led_count': 10,
        'sw_version': 'RS21-W08-MC SW',
        'hw_version': 'RS21-W08-HW SM-C',
        'hw_sub': 'U-V12',
        # Wheel serials — zeroed placeholders (real serials redacted).
        'serial0': 'VGS00000000000',
        'serial1': '00000000000000',
        'caps': bytes([0x01, 0x02, 0x1f, 0x01]),
        'hw_id': bytes.fromhex('000000000000000000000000'),
        # Real VGS emits 3 7c:23 page frames on connect (see
        # connect-wheel-start-game.pcapng). Different byte layout than CSP.
        'emits_7c23': True,
        '_7c23_frames_name': 'VGS',
        'session_layout': 'vgs_combined',
        # Factory configJson state file (session 0x09 device→host push).
        # VGS uses W17 RGB factory file. Verified 2026-04-25 against
        # `usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng`
        # blob00 from the user's real VGS — productType="W17 Display",
        # configJsonList=[Core, Grids, Mono, ..., Rally V6] (11 entries).
        # Earlier swap to W08 SM broke compat: w08_sm.json declares
        # productType="Display" + 12 different dash names (Formula 1, JDM
        # Gauge Style, etc.) which the user's VGS+display does not advertise.
        'factory_state_file': _FACTORY_STATE_FILE_W17_RGB,
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
        'rpm_led_count': 10,
        'button_led_count': 10,
        'sw_version': 'RS21-W04-MC SW',
        'hw_version': 'RS21-W04-HW SM-C',
        'hw_sub': 'U-V04B',
        # Serials redacted.
        'serial0': 'KS00000000000000',
        'serial1': 'KS00000000000001',
        'caps': bytes([0x01, 0x02, 0x1a, 0x00]),
        'hw_id': bytes.fromhex('000000000000000000000000'),
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
    'es': {
        # MOZA ES (old-protocol) wheel on R5 base. ES wheels share device ID
        # 0x13 with the wheelbase — identity probes to wheel device 0x17
        # return nothing, queries to 0x13 return base identity (see
        # docs/moza-protocol.md § ES wheel identity caveat). Identity bytes
        # captured 2026-04-23 from real R5+ES via probes to 0x13.
        #
        # `wheel_device: 0x13` reroutes all wheel-keyed dispatch through
        # 0x13 (response from 0x31 = swap_nibbles(0x13)), and the sim drops
        # any frame addressed to 0x17 silently to mimic real ES behavior.
        'name': 'R5 Black # MOT-1',
        'friendly': 'ES (R5 base)',
        # ES routing: identity, plugin probes, wheel echoes all answer at 0x13
        # (base device) instead of 0x17. Sim drops anything addressed to 0x17
        # silently to mimic a real ES wheel that doesn't enumerate there.
        'wheel_device': 0x13,
        # ES wheels: brightness 0-15, RPM LEDs driven by bitmask only (see
        # MozaWheelExtensionSettings.cs / MozaLedDeviceManager.cs). Real LED
        # count varies by ES variant; 10 is the common base assumption.
        'rpm_led_count': 10,
        'button_led_count': 0,
        'sw_version': 'RS21-D05-MC WB',
        'hw_version': 'RS21-D05-HW BM-C',
        'hw_sub': 'U-V10',
        # Serials redacted (real values present in /dev/serial/by-id and the
        # 0x10:0x00/0x10:0x01 capture; placeholders here per other profiles).
        'serial0': 'ES00000000000000',
        'serial1': 'ES00000000000001',
        # Real caps: byte 2 = 0x54 — no 0x20 RGB-display bit, so PitHouse
        # skips the display sub-device probe cascade.
        'caps': bytes([0x01, 0x02, 0x54, 0x00]),
        # Real R5+ES hw_id (matches /dev/serial/by-id/usb-Gudsen_MOZA_R5_Base_*).
        'hw_id': bytes.fromhex('000000000000000000000000'),
        # ES base returns sub-byte 0x12 in dev_type (VGS/CSP=0x04, KS=0x05).
        'dev_type': bytes([0x01, 0x02, 0x12, 0x08]),
        # identity_11 = 04:01 (matches VGS/CSP default; no override needed).
        # No dashboard, no 7c:23 frames, no display block.
        'emits_7c23': False,
        'session_layout': 'legacy',
    },
    'csp': {
        'name': 'W17',
        'friendly': 'CS Pro',
        'rpm_led_count': 18,
        'button_led_count': 14,
        'sw_version': 'RS21-W17-MC SW',
        'hw_version': 'RS21-W17-HW SM-C',
        'hw_sub': 'U-V12',
        # Real values from usb-capture/latestcaps/pithouse-switch-list-
        # delete-upload-reupload.pcapng. Byte-exact match so PitHouse's
        # cache key aligns with the real CSP wheel identity.
        'serial0': 'CSP000000000000',
        'serial1': 'CSP000000000001',
        'caps': bytes([0x01, 0x02, 0x3f, 0x01]),
        # hw_id from cmd 0x06 response (12B): 80 31 3b c0 00 20 30 04 4a 36 30 34 ("J604" tail)
        'hw_id': bytes.fromhex('000000000000000000000000'),
        # dev_type from cmd 0x04 response (4B): 01 02 06 06 — differs from
        # sim default 01 02 04 06 in position 2 (06 not 04).
        'dev_type': bytes([0x01, 0x02, 0x06, 0x06]),
        'emits_7c23': True,
        '_7c23_frames_name': 'CSP',
        # Hub (0x12) + base (0x13) + wheel (0x17) identity cascade plus all
        # session-port reads extracted from a CSP-on-R9 capture. PitHouse
        # probes hub/base with the same identity cmd set as the wheel.
        'replay_tables': [
            'sim/replay/csp_r9_wheel_17.json',
            'sim/replay/csp_r9_base_13.json',
            'sim/replay/csp_r9_hub_12.json',
        ],
        # Factory configJson state file. CSP shares the W17/RGB-DU-V11
        # display module with VGS — same 11-dashboard set. Extracted from
        # latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng.
        'factory_state_file': _FACTORY_STATE_FILE_W17_RGB,
        # Hub (0x12) + base (0x13) identity — PitHouse probes both addresses
        # with the same identity cascade (02/04/05/06/07/08/09/0F/10/11). Real
        # wheelbase returns identical values on both. Extracted byte-exact from
        # csp_r9_hub_12.json / csp_r9_base_13.json.
        'base_identity': {
            # 20-char name — splits across 07:01 ("R9 Black # MOT-1") and 07:02 ("-V01").
            'name': 'R9 Black # MOT-1-V01',
            'hw_version': 'RS21-D01-HW BM-C',
            'hw_sub': 'U-V40',
            'sw_version': 'RS21-D01-MC WB',
            'serial0': 'R9BASE0000000000',
            'serial1': 'R9BASE0000000001',
            'hw_id': bytes.fromhex('000000000000000000000000'),
            'caps': bytes([0x01, 0x02, 0x50, 0x00]),
            'dev_type': bytes([0x01, 0x02, 0x0e, 0x09]),
        },
        'session1_desc': bytes.fromhex(
            '0701000000000c048ae5d086b2fcad7486dbe208041001'
            '0a0164000000050004020000000000000006 00'
            .replace(' ', '')),
        'display': {
            'name': 'W17 Display',
            'sw_version': 'RS21-W17-HW RGB-',
            'hw_version': 'RS21-W17-HW RGB-',
            'hw_sub': 'DU-V11',
            # Real display serials from same capture (cmd 0x10 via grp 0x43).
            'serial0': 'CSPDISPLAY000000',
            'serial1': 'CSPDISPLAY000001',
            # dev_type from cmd 0x04 response: 01 02 11 06 (position 2 = 0x11,
            # not sim default 0x0d). Capture-verified.
            'dev_type': bytes([0x01, 0x02, 0x11, 0x06]),
            'caps': bytes([0x01, 0x02, 0x00, 0x00]),
            'hw_id': bytes.fromhex('8ae5d086b2fcad7486dbe208'),
        },
    },
    'kspro': {
        # MOZA KS Pro race wheel (RS21-W18). Shares the W17-HW RGB display
        # module with CSP (same 12B display hw_id). Identity strings + session
        # description chunks extracted from
        # usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng (frames ~19500–19700,
        # full PitHouse handshake). Wheel-head dev_type byte-exact from
        # capture grp=0x84 reply (`01 02 07 07`); the session2 catalog
        # carries a separate model byte (0x05 at desc position 7).
        'name': 'W18',
        'friendly': 'KS Pro',
        # LED counts from probe enumeration in capture: page 0 sub=0 reads
        # cover ff00..ff11 (18 entries), page 1 sub=1 reads cover ff00..ff09
        # (10 entries). Verify against real wheel.
        'rpm_led_count': 18,
        'button_led_count': 10,
        'sw_version': 'RS21-W18-MC SW',
        'hw_version': 'RS21-W18-HW SM-C',
        'hw_sub': 'U-V10',
        # Serials redacted.
        'serial0': 'KSP0000000000000',
        'serial1': 'KSP0000000000001',
        # Wheel-head caps + hw_id + dev_type byte-exact from
        # putOnWheelAndOpenPitHouse.pcapng grp=0x85/0x86/0x84 replies. Earlier
        # values were stale CSP-display copies; PitHouse classifies the wheel
        # off these bytes and demotes from "KS Pro" to a generic model when
        # they don't match. Verified 2026-04-26.
        'caps': bytes([0x01, 0x02, 0x41, 0x01]),
        'hw_id': bytes.fromhex('000000000000000000000000'),
        'dev_type': bytes([0x01, 0x02, 0x07, 0x07]),
        'emits_7c23': True,
        # KS Pro page-activate frames (3 variants, `fc 03` trailer) byte-exact
        # from putOnWheelAndOpenPitHouse.pcapng. Differ from CSP's 2-variant
        # `fe 01` frames; PitHouse appears to gate dashboard detection on
        # the wheel-specific trailer.
        '_7c23_frames_name': 'KSPRO',
        'session_layout': 'vgs_combined',
        'catalog_pcapng': 'usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng',
        # KS Pro pcap (putOnWheelAndOpenPitHouse.pcapng) only captures the
        # first 12s and shows the device-side OPEN with no state push, but
        # PitHouse won't detect the wheel display without the state push —
        # the real wheel must emit it later in the session. Sim pushes
        # eagerly to keep the dashboard manager UI populated.
        'proactive_session09': True,
        # KS Pro / 2026-04 firmware moves configJson state push from
        # session 0x09 to 0x0a (verified in
        # `usb-capture/ksp/mozahubstartup.pcapng` — session 0x09 carries
        # only zero-payload heartbeats, session 0x0a carries the 14.6 KB
        # Schema A snapshot at connect, Schema B deltas during uploads,
        # and the host's `{"configJson()":{...}, "id":11}` reply).
        # Tile-server also relocated: host→dev push moved from 0x03 to
        # 0x04 (12-byte envelope, sizes 775/3041/6301), and the wheel
        # now emits a dev→host mirror on 0x0b (12-byte envelope,
        # `root: "/home/moza/resource/tile_map/"`). Pushing configJson
        # on 0x09 leaves PitHouse with no parsed state → dashboard
        # manager UI shows wheel as empty.
        'configjson_session': 0x0a,
        # KS Pro shares the W17/RGB-DU-V11 display PCB with CSP but
        # firmware ships a different factory dashboard set: 10 entries
        # (Rally V1..V6 + Core/Mono/Pulse/Grids), NO Nebula. Captured
        # byte-exact 2026-04-26 from
        # `usb-capture/ksp/mozahubstartup.pcapng` seq 11..69.
        'factory_state_file': _FACTORY_STATE_FILE_KSPRO,
        # Per-device replay tables (JSON) layered over the default replay
        # source. Earlier tables win when keys collide. Pedal table is loaded
        # because real KS Pro captures include pedal traffic on 0x19 that
        # PitHouse expects a response to.
        'replay_tables': [
            'sim/replay/kspro_wheel_17.json',
            'sim/replay/kspro_base_13.json',
            'sim/replay/kspro_hub_12.json',
            'sim/replay/kspro_pedal_19.json',
        ],
        # Hub (0x12) + base (0x13) identity — R12 wheelbase. Byte-exact from
        # kspro_hub_12.json (identical to kspro_base_13.json).
        'base_identity': {
            'name': 'R12 Black # MOT-1-V01',
            'hw_version': 'RS21-D07-HW BM-C',
            'hw_sub': 'U-V10',
            'sw_version': 'RS21-D07-MC WB',
            'serial0': 'R12BASE000000000',
            'serial1': 'R12BASE000000001',
            'hw_id': bytes.fromhex('000000000000000000000000'),
            'caps': bytes([0x01, 0x02, 0x4b, 0x00]),
            'dev_type': bytes([0x01, 0x02, 0x0e, 0x09]),
        },
        # Pedal (0x19) identity — SRP pedals (only device answering 0x19 in
        # the KS Pro capture). Byte-exact from kspro_pedal_19.json.
        'pedal_identity': {
            'name': 'SRP',
            'hw_version': 'RS21-D01-HW PM-C',
            'hw_sub': 'U-V11',
            'sw_version': 'RS21-D01-MC PB',
            'serial0': 'SRPPEDAL00000000',
            'serial1': 'SRPPEDAL00000001',
            'hw_id': bytes.fromhex('000000000000000000000000'),
            'caps': bytes([0x01, 0x02, 0x18, 0x00]),
            'dev_type': bytes([0x01, 0x02, 0x02, 0x05]),
            # Pedal returns 00:04 on cmd 09, while hub/base return 00:01.
            'identity_09': bytes([0x00, 0x04]),
        },
        # Byte-for-byte from capture session 0/port 2 frags 6..10
        # (chunk sizes 26/5/2/9/2 → 'vgs_combined' layout).
        'session1_desc': bytes.fromhex(
            '0701000000000c058ae5d086b2fcad7486dbe2080410120 10a00'  # 26B
            '0164000000'                                              # 5B
            '0500'                                                    # 2B
            '040000000000000000'                                      # 9B
            '0600'                                                    # 2B
            .replace(' ', '')),
        'display': {
            'name': 'W18 Display',
            # KS Pro display reports the same RGB-I/RGB-B board strings as CSP.
            'sw_version': 'RS21-W17-HW RGB-',
            'hw_version': 'RS21-W17-HW RGB-',
            'hw_sub': 'DU-V11',
            # Serials redacted.
            'serial0': 'KSPDISPLAY000000',
            'serial1': 'KSPDISPLAY000001',
            # Display dev_type byte-exact from
            # putOnWheelAndOpenPitHouse.pcapng grp=0xC3 wrapper grp=0x84 reply.
            'dev_type': bytes([0x01, 0x02, 0x11, 0x06]),
            'caps': bytes([0x01, 0x02, 0x00, 0x00]),
            'hw_id': bytes.fromhex('8ae5d086b2fcad7486dbe208'),
        },
    },
}

def _build_device_identity(dev_id: int, ident: dict) -> Dict[Tuple[int, int, bytes], bytes]:
    """Build a (device, group, payload) → response map covering the identity
    probe cascade for one non-wheel device (hub/base/pedal).

    Groups answered: 02, 04, 05, 06, 07, 08, 09, 0F, 10, 11. Matches what
    PitHouse sends per-device during post-connect enumeration.

    `ident` keys: name, hw_version, hw_sub, sw_version, serial0, serial1,
    hw_id (12B), caps (4B), dev_type (4B), [identity_09 (2B, default 00:01)].
    """
    slices = _id_slices(ident['name'])
    entries: Dict[Tuple[int, int, bytes], bytes] = {
        # Real frames carry a 1-byte `00` payload on cmd 02 probes — length byte
        # = 1. Don't key on empty `b''` (won't match).
        (dev_id, 0x02, b'\x00'):             bytes([0x02]),
        (dev_id, 0x04, b'\x00\x00\x00\x00'): ident['dev_type'],
        (dev_id, 0x05, b'\x00\x00\x00\x00'): ident['caps'],
        (dev_id, 0x06, b''):                 ident['hw_id'],
        (dev_id, 0x07, b'\x01'):             bytes([0x01]) + slices[0],
        (dev_id, 0x08, b'\x01'):             bytes([0x01]) + _id_str(ident['hw_version']),
        (dev_id, 0x08, b'\x02'):             bytes([0x02]) + _id_str(ident['hw_sub']),
        (dev_id, 0x09, b''):                 ident.get('identity_09', bytes([0x00, 0x01])),
        (dev_id, 0x0F, b'\x01'):             bytes([0x01]) + _id_str(ident['sw_version']),
        (dev_id, 0x10, b'\x00'):             bytes([0x00]) + _id_str(ident['serial0']),
        (dev_id, 0x10, b'\x01'):             bytes([0x01]) + _id_str(ident['serial1']),
        (dev_id, 0x11, b'\x04'):             bytes([0x04, 0x01]),
    }
    # Multi-slice name (real hub/base return "R9 Black # MOT-1-V01" = 20 chars
    # split across 07:01 + 07:02).
    if len(ident['name']) > 16:
        entries[(dev_id, 0x07, b'\x02')] = bytes([0x02]) + slices[1]
    return entries


def _build_identity_tables(model: dict) -> Tuple[
    Dict[Tuple[int, int, bytes], bytes],
    Dict[Tuple[int, bytes], bytes],
    Dict[Tuple[int, int, bytes], bytes],
]:
    """Build plugin probe, wheel identity, and per-device identity tables.

    Returns (plugin_rsp, pithouse_rsp, device_rsp). `device_rsp` covers
    hub/base/pedal identity probes — populated from model's base_identity
    and pedal_identity blocks. Hub and base share the same answers (real
    wheelbase returns identical values on both addresses).

    For ES wheels (wheel_device=0x13), wheel identity entries are keyed by 0x13
    instead of 0x17 — real ES wheels share device ID with the wheelbase and do
    not respond at 0x17 at all.
    """
    name = model['name']
    disp = model.get('display', {})
    wheel_dev = model.get('wheel_device', DEV_WHEEL)

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
        # ── Wheel identity reads (model-specific; ES → 0x13 not 0x17) ──
        (0x07, wheel_dev, b'\x01'): b'\x01' + _id_str(name),
        (0x0F, wheel_dev, b'\x01'): b'\x01' + _id_str(model['sw_version']),
        (0x08, wheel_dev, b'\x01'): b'\x01' + _id_str(model['hw_version']),
        (0x08, wheel_dev, b'\x02'): b'\x02' + _id_str(model['hw_sub']),
        (0x10, wheel_dev, b'\x00'): b'\x00' + _id_str(model['serial0']),
        (0x10, wheel_dev, b'\x01'): b'\x01' + _id_str(model['serial1']),
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
            # Short-form identity probes (no sub-byte). PitHouse sends these
            # alongside the sub-byte variants during display negotiation on
            # 2025-11+ firmware; wheel answers with the sub=0x01 (or 0x00 for
            # serial) payload. Without these entries PitHouse marks the
            # display as "not fully detected" in Dashboard Manager even
            # though the sub-byte probes all got answers. Byte-exact from
            # usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng.
            (0x43, 0x17, b'\x07'):             bytes([0x87, 0x01]) + _id_str(disp.get('name', '')),
            (0x43, 0x17, b'\x08'):             bytes([0x88, 0x01]) + _id_str(disp['hw_version']),
            (0x43, 0x17, b'\x0f'):             bytes([0x8f, 0x01]) + _id_str(disp['sw_version']),
            (0x43, 0x17, b'\x10'):             bytes([0x90, 0x00]) + _id_str(disp['serial0']),
            (0x43, 0x17, b'\x11'):             bytes([0x91, 0x04]),
        })

    # PitHouse identity probes (groups 0x02–0x11, device 0x17)
    pithouse_rsp: Dict[Tuple[int, bytes], bytes] = {
        (0x09, b''):                 bytes([0x00, 0x01]),
        # Real 02 probes carry a 1-byte `00` payload (length=1). Key was
        # historically `b''` which never hit — replay covered it. Procedural
        # now, replay only needed for wheels without JSON tables.
        (0x02, b'\x00'):             bytes([0x02]),
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

    # Per-device identity (hub 0x12, base 0x13, pedal 0x19). Skipped when a
    # model has no base_identity/pedal_identity block — VGS/KS currently rely
    # on replay for hub/base probes. ES (wheel_device=0x13) uses its own
    # wheel identity for the base address.
    device_rsp: Dict[Tuple[int, int, bytes], bytes] = {}
    base_ident = model.get('base_identity')
    if base_ident:
        device_rsp.update(_build_device_identity(0x12, base_ident))
        device_rsp.update(_build_device_identity(0x13, base_ident))
    pedal_ident = model.get('pedal_identity')
    if pedal_ident:
        device_rsp.update(_build_device_identity(0x19, pedal_ident))

    return plugin_rsp, pithouse_rsp, device_rsp

# Built at startup from selected --model. Populated by main().
_PLUGIN_PROBE_RSP: Dict[Tuple[int, int, bytes], bytes] = {}
_PITHOUSE_ID_RSP: Dict[Tuple[int, bytes], bytes] = {}
_DEVICE_ID_RSP: Dict[Tuple[int, int, bytes], bytes] = {}
_DISPLAY_MODEL_NAME: str = 'Display'
# Selected wheel device (0x17 for new-protocol wheels, 0x13 for ES which shares
# the wheelbase address). Populated by main() from the chosen model profile.
_WHEEL_DEVICE: int = DEV_WHEEL

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
# KS Pro page-activate frames byte-exact from
# usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng (3 variants emitted by
# the real wheel during connection). Differ from CSP in trailer bytes
# (`fc 03` vs CSP's `fe 01`) and use 3 pages instead of 2 — earlier sim
# was sending CSP frames for KS Pro which PitHouse appears to reject for
# its dashboard-detection check.
_7C_23_FRAMES_KSPRO = [
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x32\x80\x04\x00\x01\x00\xfc\x03'),  # KS Pro page 1
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x3c\x80\x05\x00\x02\x00\xfc\x03'),  # KS Pro page 2
    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                b'\x7c\x23\x50\x80\x06\x00\x03\x00\xfc\x03'),  # KS Pro page 3
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

# Real wheel session 0x04 type=0x0a dir-listing reply layout (verified
# against latestcaps + connect-wheel-start-game captures, see
# docs/moza-protocol.md § Session 0x04 device → host root directory listing):
#
#   offset  bytes                              meaning
#   0x00    0a                                 subtype
#   0x01    <size_LE:4>                        body bytes after this field
#   0x05    00 00 00 <pathlen_BE:1> 00         path length marker (5B)
#   0x0a    <UTF-16LE path>                    20B for "/home/root"
#   0x1e    ff ff ff ff ff ff ff ff 00         9B padding sentinel
#   0x27    de c3 90 00 00 00 00 00 00 00      10B firmware-fingerprint nonce
#   0x31    <unknown_u32_LE:4>                 100,521 latestcaps / 273,966
#                                              older — only field varying
#                                              between firmware revisions.
#                                              Storage-display candidate;
#                                              encoded dynamically per
#                                              FS state.
#   0x35    [zlib stream]                      deflate of JSON body
#
# Real wheel does NOT echo the request's 8-byte echo_id back in the reply.
# Pre-2026-04-25 sim wrongly inserted echo_id at offset 0x26, displacing the
# 14B metadata block and bloating the reply by 8B.
# Real-wheel u32 at prefix offset 48-51 (post-zeros, pre-trailing). Constant
# within firmware revision: 100,521 for current 2025-11 firmware, 273,454
# for older. Role not yet decoded — emitted byte-exact so PitHouse parser
# accepts the reply. Storage-display value isn't this u32 (verified by
# updating sim and observing PitHouse's storage UI didn't change), but
# wrong value here may cause PitHouse to reject the dir-listing entirely.
_DIR_LISTING_U32 = 100521


def _build_dir_listing_body_json(temp_children: Optional[list] = None) -> bytes:
    """JSON body for `/home/root` listing — real wheel always reports just
    `temp/` (dashboards live elsewhere; `/home/root` is the temp transfer
    workspace). `root.fileSize` is **always 0** in real captures — sim
    previously injected `used_bytes` here which doesn't match the wire
    format and may cause PitHouse to reject the reply.

    `temp_children`: optional list of child entries currently in the
    `/home/root/temp/` workspace (active uploads). Real captures show
    either empty `temp.children` (idle wheel) or 1-3 entries named
    `_moza_filetransfer_md5_<hex>` for in-flight transfers.
    """
    now_ms = int(time.time() * 1000)
    body = {
        "children": [{
            "children": list(temp_children or []),
            "createTime": -28800000,
            "fileSize": 0,
            "md5": "d41d8cd98f00b204e9800998ecf8427e",
            "modifyTime": now_ms,
            "name": "temp",
        }],
        "createTime": -28800000,
        "fileSize": 0,
        "md5": "",
        "modifyTime": now_ms,
        "name": "root",
    }
    return json.dumps(body, separators=(',', ':')).encode('utf-8')


def build_dir_listing_reply(echo_id: bytes = b'',
                            empty: bool = False,
                            used_bytes: int = 0,
                            temp_in_progress: bool = False) -> bytes:
    """Build the type=0x0a directory-listing reply for the host's
    `/home/root` query on session 0x04. Real wheel doesn't echo `echo_id`
    in the reply — kwarg kept for back-compat but ignored.

    Verified prefix layout (53B, byte-exact match across 6 captures —
    see usb-capture/payload-09-state-re.md once updated):
        offset    bytes              meaning
        0         0a                 subtype
        1-4       <size_LE:u32>      bytes after this field
        5-9       00 00 00 14 00     path-length marker (5B)
        10-28     <UTF-16 path>      19B "/home/roo" (firmware quirk:
                                     trailing UTF-16 null omitted)
        29-36     ff ff ff ff ff ff ff ff   8B sentinel
        37        00                 1B separator (varies; observed 00
                                     and 02 — role not decoded)
        38-40     <3B nonce>         random per-session — sim uses fixed
                                     `de c3 90` for now; vary if needed
        41-46     00 00 00 00 00 00  6B zeros
        47        <1B temp_flag>     0x00 when /home/root/temp is empty,
                                     0x01 when transfer files reside there
        48-51     <u32 LE>           100,521 / 273,454 — role unknown but
                                     constant per firmware revision
        52        00                 1B trailing
        53+       [zlib stream]      JSON body

    `used_bytes` retained as kwarg for callers but no longer written into
    the prefix — its earlier role assumption (storage value) didn't hold
    up against PitHouse's storage display (688/6486 MB persisted across
    sim u32 changes).
    """
    del echo_id, empty, used_bytes  # accepted for back-compat, unused
    body_json = _build_dir_listing_body_json()
    zbody = zlib.compress(body_json)
    prefix = bytearray()
    prefix.append(0x0a)                                       # 1B subtype
    prefix += b'\x00\x00\x00\x00'                             # 4B size_LE placeholder
    prefix += b'\x00\x00\x00\x14\x00'                         # 5B path-length marker
    prefix += '/home/root'.encode('utf-16-le')[:19]           # 19B path
    prefix += b'\xff' * 8                                     # 8B sentinel
    prefix += b'\x00'                                         # 1B separator
    prefix += bytes.fromhex('dec390')                         # 3B nonce
    prefix += b'\x00' * 6                                     # 6B zeros
    prefix.append(0x01 if temp_in_progress else 0x00)         # 1B temp flag
    prefix += struct.pack('<I', _DIR_LISTING_U32)             # 4B u32 (firmware constant)
    prefix += b'\x00'                                         # 1B trailing
    body = bytes(prefix) + zbody
    size_after = len(body) - 5
    body = body[:1] + struct.pack('<I', size_after) + body[5:]
    return body

# Delay after last session 0x04 chunk before emitting the sub-msg ack response.
# Short enough that both sub-msg 1 and sub-msg 2 responses fit inside the plugin's
# per-submsg wait windows (2 s and 3 s). See docs/moza-protocol.md §
# "Sub-msg 1 / sub-msg 2 response format".
_FILE_TRANSFER_ECHO_IDLE_MS = 100


def build_file_transfer_response(
    remote_path: str,
    local_path: str,
    md5: bytes,
    total_size: int,
    bytes_written: int,
    *,
    submsg_index: int,
) -> bytes:
    """Build a device→host sub-msg 1 or sub-msg 2 response body for session 0x05/0x06.

    Wire format (verified 2026-04-24 against the four replies in
    latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng, both files).
    The message is a 6-byte header + body (size field counts body bytes):

        [type:1] [size_LE:u32] [pad:1=0x00]              (6B header)
        [pad:2 = 0x00 0x00]                              (body starts here)
        [0x70 0x00] [UTF-16LE remote path] [0x00 0x00]   (REMOTE TLV)
        [0x8C 0x00] [UTF-16LE local path]  [0x00 0x00]   (LOCAL TLV)
        [0x10] [md5:16]
        [bytes_written:u32 BE] [total_size:u32 BE]
        [0xFF 0xFF 0xFF 0xFF]                            (sentinel)
        [status:1]                                       (XOR of body)

    The "4-byte trailer" earlier docs chased was a misread: only 1 byte of
    status follows the sentinel, and the next 3 bytes belong to the chunk's
    truncated CRC32 (see `chunk_session_payload`). The status byte is the
    8-bit XOR over the body bytes (excluding the status byte itself); for
    file2 in the reference capture XOR returns 0x2e / 0x74 matching the
    wire bytes.

    Sim historically treated the header as 8B and emitted 4B ff*4 trailer,
    which made each reply 3B too long and shifted every size/offset PitHouse
    tried to parse. Current code keeps the 8B header shape for caller
    compatibility but emits `size = body_len + 2` so PitHouse's 6B-header
    parser lands on the same byte boundary.

    `submsg_index` is 1 (path-registration ack, role=0x01) or 2 (content-complete
    ack, role=0x11).
    """
    if submsg_index not in (1, 2):
        raise ValueError('submsg_index must be 1 or 2')
    role = 0x01 if submsg_index == 1 else 0x11
    if len(md5) != 16:
        raise ValueError('md5 must be 16 bytes')

    body_after_header = bytearray()
    body_after_header.append(0x70)
    body_after_header.append(0x00)
    body_after_header.extend(remote_path.encode('utf-16-le'))
    body_after_header.extend(b'\x00\x00')
    body_after_header.append(0x8C)
    body_after_header.append(0x00)
    body_after_header.extend(local_path.encode('utf-16-le'))
    body_after_header.extend(b'\x00\x00')
    body_after_header.append(0x10)
    body_after_header.extend(md5)
    body_after_header.extend(bytes_written.to_bytes(4, 'big'))
    body_after_header.extend(total_size.to_bytes(4, 'big'))
    body_after_header.extend(b'\xff\xff\xff\xff')

    # 1-byte XOR status: XOR of every byte emitted so far in body_after_header.
    # (The real wheel also XORs the 2B `00 00` preamble between header and TLV,
    # but XOR with 0 is a no-op so skipping those bytes yields the same value.)
    status = 0
    for b in body_after_header:
        status ^= b
    body_after_header.append(status)

    # 8B header trick: `size_LE` counts from (sim header end - 2) to end of
    # body, so PitHouse's 6B-header parser reads the last 2 pad bytes of the
    # 8B header as the body's `00 00` preamble. Reverted from 6B-header
    # 2026-04-26 — direct 6B emission stalled PitHouse uploads at chunk 1.
    # See moza-protocol.md § Findings 2026-04-24 for the trick rationale.
    size = len(body_after_header) + 2
    header = bytes([role]) + size.to_bytes(4, 'little') + bytes([0x00, 0x00, 0x00])
    return header + bytes(body_after_header)

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
    """Accumulate 7C:00 type=0x01 data chunks, strip truncated-CRC32 trailer,
    reassemble.

    Pithouse sends two chunk formats on 7c:00 data: (a) [payload][CRC] where
    CRC covers the full payload (used for standalone small messages + the simhub
    plugin's tier def), and (b) [flag:1][payload][CRC] where CRC covers the
    flag+payload and the flag byte is a chunk-level session marker that must be
    stripped before concatenation (used for pithouse's multi-chunk tier def
    uploads on session 0x01). CRC match decides which format applies.

    Real wheel and PitHouse use a 3-byte truncated CRC32 (first 3 bytes of
    zlib.crc32(net) LE). Legacy 4-byte CRC fallback kept for compatibility
    with older tooling/captures."""
    def __init__(self):
        self._chunks: Dict[int, bytes] = {}

    def add(self, seq: int, raw: bytes):
        if len(raw) < 4:
            self._chunks[seq] = raw
            return
        crc4 = int.from_bytes(raw[-4:], 'little')
        if zlib.crc32(bytes(raw[:-4])) == crc4:
            self._chunks[seq] = raw[:-4]
            return
        if len(raw) >= 5 and zlib.crc32(bytes(raw[1:-4])) == crc4:
            self._chunks[seq] = raw[1:-4]
            return
        if len(raw) >= 3:
            crc3 = raw[-3:]
            if zlib.crc32(bytes(raw[:-3])).to_bytes(4, 'little')[:3] == crc3:
                self._chunks[seq] = raw[:-3]
                return
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
    """Wrap a JSON RPC reply in the session 0x0a wire format. 9-byte prefix
    (same shape as session 0x09 configJson state push):

        [tag:1 = 0x00] [comp_size+4 LE:4] [uncomp_size LE:4] [zlib stream]

    Documented in usb-capture/session-0x0a-rpc-re.md — confirmed across 5
    captured reset-RPC blobs with byte-identical envelope `00 1d 00 00 00
    11 00 00 00`. The `+4` on the compressed-size field comes from the
    real capture (comp_size=25 → field=29); PitHouse uses the value to
    delimit the zlib stream so we match exactly."""
    body = json.dumps(obj, separators=(',', ':')).encode('utf-8')
    comp = zlib.compress(body)
    hdr = (b'\x00'
           + struct.pack('<I', len(comp) + 4)
           + struct.pack('<I', len(body)))
    return hdr + comp


def _factory_hash_to_md5(hash_field: str) -> str:
    """Decode a factory state `hash` field to its canonical MD5 hex string.

    Real wheel encodes the dashboard MD5 as `bytes(md5_hex, 'ascii').hex()`
    — i.e. hex-of-ASCII-of-MD5. e.g. `"6237346230306537..."` decodes to ASCII
    `"b74b00e7..."` (the actual 32-char MD5 hex). See
    usb-capture/payload-09-state-re.md § Schema A `hash` field.
    """
    if not hash_field:
        return ''
    try:
        return bytes.fromhex(hash_field).decode('ascii')
    except (ValueError, UnicodeDecodeError):
        return hash_field


class WheelFileSystem:
    """Tracks user-installed content the simulator persists across restarts:
    uploaded dashboards, tile server caches. Mirrors a real wheel's writable
    FS partition (`/home/moza/resource/...`).

    Factory dashboards are NOT in this FS. Real wheel ships them in
    firmware ROM, not as queryable files (verified: `/home/root` listing on
    real wheel returns just `temp/`). The sim's `factory_state_<...>.json`
    plays the ROM role — `build_configjson_state` reads it directly when
    building the session 0x09 state push and merges any FS-tracked uploads
    on top. Starts empty on first sim run; grows only as PitHouse uploads
    new dashboards.

    Files stored as raw bytes with md5 + mtime metadata. Directories are
    implicit (derived from file paths). Persisted to sim/logs/wheel_fs.json.

    Paths the sim writes (matches real wheel `rootDirPath`):
      /home/moza/resource/dashes/<name>/<name>.mzdash               — body
      /home/moza/resource/dashes/<name>/<name>.mzdash_v2_10_3_05.png — preview
      /home/moza/resource/tile_server/<game>/...                    — tiles
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
        # Defer disk writes inside `with batch_writes()` blocks so a multi-file
        # bundle upload doesn't rewrite the whole wheel_fs.json N times.
        if getattr(self, '_save_deferred', 0) > 0:
            self._save_pending = True
            return
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
            # Atomic write: tmp + os.replace so a crash mid-write can't corrupt
            # the persistent FS file (1.5MB+ and growing — partial JSON would
            # fail to load on next sim start).
            import os as _os
            tmp = self._persist_path.with_suffix(self._persist_path.suffix + '.tmp')
            tmp.write_text(json.dumps(out, indent=2))
            _os.replace(str(tmp), str(self._persist_path))
        except Exception:
            pass

    @contextlib.contextmanager
    def batch_writes(self):
        """Defer `_save` to the end of the block. Use for multi-file bundle
        uploads (mzdash + N PNGs) where each individual `write_file` would
        otherwise trigger a full rewrite of wheel_fs.json."""
        self._save_deferred = getattr(self, '_save_deferred', 0) + 1
        try:
            yield
        finally:
            self._save_deferred -= 1
            if self._save_deferred == 0 and getattr(self, '_save_pending', False):
                self._save_pending = False
                self._save()

    def write_file(self, path: str, data: bytes, md5_override: Optional[str] = None) -> None:
        import hashlib
        now_ms = int(time.time() * 1000)
        prev = self._files.get(path)
        create = prev['create'] if prev else now_ms
        self._files[path] = {
            'bytes': bytes(data),
            # Allow md5 override so factory-populated files report canonical
            # hashes PitHouse expects, without needing the real mzdash bytes.
            'md5': md5_override or hashlib.md5(data).hexdigest(),
            'mtime': now_ms,
            'create': create,
        }
        self._save()

    def populate_single_stub_dashboard(self, dname: str = 'Core',
                                       factory_file: Optional[str] = None) -> dict:
        """Install ONE stub dashboard matching the schema PitHouse expects in
        enableManager.dashboards. Pulls metadata for `dname` from EITHER factory
        state file (W17/RGB or W08/SM display set) — PitHouse's library may
        recognise dashboards from either firmware vintage. Returns the
        metadata dict for use in build_configjson_state's enableManager."""
        meta = None
        for fs_file in (factory_file, _FACTORY_STATE_FILE_W17_RGB,
                        _FACTORY_STATE_FILE_W08_SM):
            if not fs_file:
                continue
            factory = _load_factory_configjson_state(fs_file)
            for d in factory.get('enableManager', {}).get('dashboards', []) or []:
                if (d.get('dirName') or d.get('title', '')) == dname:
                    meta = d
                    break
            if meta:
                break
        if meta is None:
            # Fall back to first entry of primary factory file.
            factory = _load_factory_configjson_state(factory_file)
            factory_dashboards = factory.get('enableManager', {}).get('dashboards', [])
            if factory_dashboards:
                meta = factory_dashboards[0]
                dname = meta.get('dirName') or meta.get('title', 'Unknown')
        if meta is None:
            return {}
        canonical_md5 = _factory_hash_to_md5(meta.get('hash', ''))
        mzdash_path = f'{_WHEEL_FW_DASH_ROOT}/{dname}/{dname}.mzdash'
        preview_path = f'{_WHEEL_FW_DASH_ROOT}/{dname}/{dname}.mzdash_v2_10_3_05.png'
        self.write_file(mzdash_path, b'\x00' * 1024, md5_override=canonical_md5)
        self.write_file(preview_path, b'\x00' * 8192)
        return meta

    def purge_legacy_dash_paths(self) -> int:
        """Remove any FS entries under `/home/root/resource/dashes/` (legacy
        path layout from sim builds before the 2026-04-25 migration to
        `/home/moza/...`). Returns count removed. Called once at sim init
        to clean stale persistent state.
        """
        legacy = [p for p in list(self._files)
                  if p.startswith('/home/root/resource/dashes/')]
        for p in legacy:
            del self._files[p]
        if legacy:
            self._save()
        return len(legacy)

    def seed_factory_dashboards(self, factory_file: Optional[str] = None,
                                stub_mzdash_size: int = 1024,
                                stub_preview_size: int = 8192) -> int:
        """Opt-in test helper: write one stub `.mzdash` + preview per factory
        dashboard at `/home/moza/resource/dashes/<dirName>/` with canonical
        hash bytes.

        NOT called by the sim during normal init — factory dashboards are
        ROM-resident on the real wheel and are emitted into the session
        0x09 state push by `build_configjson_state` reading factory state
        directly. This helper is only useful when a test needs the factory
        files to exist as queryable FS entries (e.g. simulating a wheel
        whose ROM contents have been "extracted" into user FS).

        Returns count of dashboards seeded. No-op if any `*.mzdash` already
        exists at `/home/moza/...` — preserves user uploads.
        """
        for p in self._files:
            if p.startswith(f'{_WHEEL_FW_DASH_ROOT}/') and p.endswith('.mzdash'):
                return 0
        factory = _load_factory_configjson_state(factory_file)
        dashes = factory.get('enableManager', {}).get('dashboards', []) or []
        seeded = 0
        for meta in dashes:
            dname = meta.get('dirName') or meta.get('title')
            if not dname:
                continue
            canonical_md5 = _factory_hash_to_md5(meta.get('hash', ''))
            mzdash_path = f'{_WHEEL_FW_DASH_ROOT}/{dname}/{dname}.mzdash'
            preview_path = (f'{_WHEEL_FW_DASH_ROOT}/{dname}/'
                            f'{dname}.mzdash_v2_10_3_05.png')
            self.write_file(mzdash_path, b'\x00' * stub_mzdash_size,
                            md5_override=canonical_md5)
            self.write_file(preview_path, b'\x00' * stub_preview_size)
            seeded += 1
        return seeded

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
        root = f'{_WHEEL_FW_DASH_ROOT}/'
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

    def total_dashboard_bytes(self) -> int:
        """Sum of bytes occupied by all files under
        `/home/moza/resource/dashes/`. Drives the dynamic u32 in the session
        0x04 dir-listing reply (storage-display candidate).
        """
        root = f'{_WHEEL_FW_DASH_ROOT}/'
        return sum(len(meta['bytes']) for p, meta in self._files.items()
                   if p.startswith(root))

    def image_manifest(self) -> Tuple[dict, list]:
        """Walk dashboard PNG/JPG asset files in `/home/moza/resource/dashes/`
        and produce the configJson state's `imageRefMap` + `imagePath`
        fields from FS contents.

        Returns `(image_ref_map, image_path)`:
          - `image_ref_map`: `{url: refcount}` where url is `<dirName>/<file>`
            (per-dashboard) or `MD5/<md5>.png` (deduped; not yet generated
            by sim — placeholder for future image dedup work). Refcount is
            the number of dashboards holding that asset; sim treats each
            file as referenced once by its owning dashboard.
          - `image_path`: list of `{md5, modify, url}` entries — actual
            files stored, in mtime order.

        On a wiped wheel both are empty.
        """
        root = f'{_WHEEL_FW_DASH_ROOT}/'
        ref_map: Dict[str, int] = {}
        path_entries: List[dict] = []
        for p, meta in self._files.items():
            if not p.startswith(root):
                continue
            rest = p[len(root):]
            if '/' not in rest:
                continue
            ext_lower = p.lower()
            if not (ext_lower.endswith('.png') or ext_lower.endswith('.jpg')):
                continue
            url = rest  # `<dirName>/<filename.ext>`
            ref_map[url] = ref_map.get(url, 0) + 1
            path_entries.append({
                'md5': meta['md5'],
                'modify': str(meta['mtime']),
                'url': url,
            })
        path_entries.sort(key=lambda e: e['url'])
        return ref_map, path_entries


# Canonical dashboard library names that PitHouse offers to the wheel (new
# firmware pushes this list back in `configJson()` host→device replies). Must
# appear in configJson state too — wheel reports this same list in
# `configJsonList` so PitHouse UI matches. Set from latestcaps capture.
_CONFIGJSON_CANONICAL_LIST = [
    "Core", "Grids", "Mono", "Nebula", "Pulse",
    "Rally V1", "Rally V2", "Rally V3", "Rally V4", "Rally V5", "Rally V6",
]


# Cache keyed on resolved file path so multiple wheel models can each load
# their own captured factory state without re-parsing.
_FACTORY_STATE_CACHE: Dict[str, dict] = {}

# Default factory file when no model-specific override was passed. Points at
# the W17/RGB-DU-V11 capture (current 2025-11 firmware) — what user's VGS and
# CSP both report. Older wheels (W08/SM-DU-V14 display) should pass
# `factory_state_w08_sm.json` explicitly via the model dict.
_DEFAULT_FACTORY_STATE_FILE = _FACTORY_STATE_FILE_W17_RGB


def _load_factory_configjson_state(factory_file: Optional[str] = None) -> dict:
    """Load a real-wheel factory configJson state JSON captured from PitHouse
    traffic. Each wheel model may select a different capture (e.g. W17/RGB
    display vs W08/SM display) — see usb-capture/payload-09-state-re.md
    for the per-display-module breakdown.

    `factory_file` is a filename (resolved relative to this module's dir).
    Falls back to `factory_configjson_state.json` (legacy) then to the
    W17/RGB default. Returns `{}` if no candidate exists.
    """
    here = Path(__file__).parent
    candidates: List[Path] = []
    if factory_file:
        candidates.append(here / factory_file)
    candidates.append(here / 'factory_configjson_state.json')
    candidates.append(here / _DEFAULT_FACTORY_STATE_FILE)
    parse_errors: List[str] = []
    for path in candidates:
        key = str(path)
        if key in _FACTORY_STATE_CACHE:
            return _FACTORY_STATE_CACHE[key]
        if path.exists():
            try:
                obj = json.loads(path.read_text())
                _FACTORY_STATE_CACHE[key] = obj
                return obj
            except Exception as e:
                parse_errors.append(f'{path.name}: {e}')
                continue
    # No valid factory file — log once. Empty factory means the configJson
    # state push goes out with empty `enableManager.dashboards` and empty
    # `configJsonList`, which PitHouse renders as a wiped wheel. Worth a
    # loud warning so the user knows why the UI looks empty.
    if parse_errors:
        print(f'[wheel_sim] WARNING: factory state load failed — '
              f'configJson state will be empty. Errors: {"; ".join(parse_errors)}',
              file=sys.stderr)
    return {}


def build_configjson_state(dashboards: List[dict], title_id: int = 1,
                           display_version: int = 11,
                           reset_version: int = 10,
                           canonical_list: Optional[List[str]] = None,
                           factory_file: Optional[str] = None,
                           image_ref_map: Optional[dict] = None,
                           image_path: Optional[list] = None,
                           display_product_type: Optional[str] = None) -> bytes:
    """Build the session 0x09 wheel→host configJson state JSON + chunk envelope.

    Every dashboard-related field is derived from the live FS state. An
    empty wheel reports empty `configJsonList`, empty `enableManager.dashboards`,
    empty `imageRefMap`/`imagePath`. Wiping a real wheel collapses all four
    to empty too — `configJsonList` is NOT firmware-baked.

    `factory_file` is consulted only as a metadata-lookup table: if any
    `dirName` in `dashboards` matches a known entry there, that entry's
    full record (hash / id / idealDeviceInfos / lastModified) is reused so
    PitHouse cache-skip behaves like real hardware on a re-upload of a
    known dashboard. Unknown names get synthesised metadata.

    `image_ref_map` and `image_path` (when provided) describe images that
    live in the FS alongside dashboards. When omitted they default to
    empty — caller is expected to compute them from `WheelFileSystem`
    contents once the image refcount logic lands. Wiped wheel → empty.

    Envelope format (verified across captures):
      [flag:1B=0x00][comp_size:u32 LE][uncomp_size:u32 LE][zlib stream]
    """
    factory = _load_factory_configjson_state(factory_file)
    factory_dashboards = factory.get('enableManager', {}).get('dashboards', []) or []
    # Merge in metadata from the OTHER firmware's factory state too. User
    # uploads observed in real captures span both firmware vintages
    # (e.g. "Lovely Dashboard for Vision GS" lives in W08/SM-DU-V14 set, not
    # W17/RGB-DU-V11). PitHouse caches per-dashboard metadata keyed on
    # dirName + hash; using a fully-populated factory record (with the
    # original `id`, `idealDeviceInfos`, `lastModified`) makes our state
    # match PitHouse's expectation for both firmware sets.
    factory_by_name = {}
    for fs_file in (_FACTORY_STATE_FILE_W17_RGB, _FACTORY_STATE_FILE_W08_SM):
        alt_factory = _load_factory_configjson_state(fs_file)
        for d in (alt_factory.get('enableManager', {}).get('dashboards', []) or []):
            name = d.get('dirName') or d.get('title', '')
            if name and name not in factory_by_name:
                factory_by_name[name] = d
    # Pull a single representative idealDeviceInfos from the factory state to
    # use on synthesised user-upload entries. Real wheel never emits an empty
    # list — PitHouse uses this to decide UI compatibility, and an empty list
    # gets the entry filtered out of the Dashboard Manager view.
    default_ideal_device_infos = []
    for fd in factory_dashboards:
        if fd.get('idealDeviceInfos'):
            default_ideal_device_infos = fd['idealDeviceInfos']
            break
    # enableManager.dashboards: factory firmware-baked entries first, then
    # FS-tracked user uploads appended. Real wheel ALWAYS reports factory
    # dashes here regardless of FS state — they're ROM, not files. Earlier
    # sim versions returned only FS uploads which left PitHouse's Dashboard
    # Manager UI showing an empty "On Device" library.
    enable_dashboards: List[dict] = []
    fs_names: List[str] = []
    enabled_names: set = set()
    # Seed with the wheel's own factory list (preserves capture-derived
    # ordering, hash, idealDeviceInfos for cache-stable PitHouse behaviour).
    for fd in factory_dashboards:
        name = fd.get('dirName') or fd.get('title', '')
        if name and name not in enabled_names:
            enable_dashboards.append(fd)
            enabled_names.add(name)
    import hashlib as _hashlib
    import time as _time
    for d in dashboards:
        name = d.get('dirName') or d.get('name') or d.get('title', '')
        if not name or name in enabled_names:
            continue
        fs_names.append(name)
        factory_meta = factory_by_name.get(name)
        if factory_meta:
            enable_dashboards.append(factory_meta)
            enabled_names.add(name)
            continue
        # Synthesise a real-wheel-shaped entry for user uploads.
        raw_md5 = d.get('hash') or _hashlib.md5(name.encode()).hexdigest()
        # Real wheel encodes hash field as hex-of-ASCII of the canonical MD5
        # hex string. e.g. md5='b74b00e7...' -> hash='6237346230...'
        encoded_hash = bytes(raw_md5, 'ascii').hex() if raw_md5 else ''
        # 32-char alnum id derived from hash to keep stable per upload (matches
        # real wheel's user-upload id format like `0ZPoaC0XdTHawiWIvXEq8wjDYjGenlMz`).
        id_seed = _hashlib.sha256(name.encode() + raw_md5.encode()).digest()
        import base64 as _b64
        upload_id = _b64.urlsafe_b64encode(id_seed)[:32].decode('ascii').replace('-', 'a').replace('_', 'b')
        # ISO-8601 timestamp for lastModified.
        last_mod_iso = _time.strftime('%Y-%m-%dT%H:%M:%SZ', _time.gmtime())
        preview_path = f'{_WHEEL_FW_DASH_ROOT}/{name}/{name}.mzdash_v2_10_3_05.png'
        enable_dashboards.append({
            'createTime': '',
            'dirName': name,
            'hash': encoded_hash,
            'id': upload_id,
            'idealDeviceInfos': list(default_ideal_device_infos),
            'lastModified': last_mod_iso,
            'previewImageFilePaths': [preview_path],
            'resouceImageFilePaths': [],
            'title': d.get('title') or name,
        })
        enabled_names.add(name)
    # configJsonList is the firmware-baked canonical catalog of dashboard
    # names this wheel supports — NOT the installed-FS mirror. PitHouse uses
    # it to drive its "Available dashboards on this wheel" library view; if
    # it's empty, PitHouse appears to fall back to a hardcoded per-model
    # default catalog (observed: VGS sim with empty list shows w08_sm-style
    # dashboards in UI). Source from the loaded factory_state file's
    # configJsonList field (firmware-stamped, immutable). See
    # `usb-capture/empty-fs-signalling.md`.
    if canonical_list is None:
        fact_canonical = factory.get('configJsonList') if factory else None
        if isinstance(fact_canonical, list) and fact_canonical:
            canonical_list = list(fact_canonical)
        else:
            canonical_list = list(_CONFIGJSON_CANONICAL_LIST)
        # Append user-uploaded dashes that aren't already in the firmware
        # catalog. PitHouse's slot-resolution (driven by 28:00 reads) appears
        # to index INTO `configJsonList` — bbb at slot 12 worked when the
        # W17/RGB factory list had 11 entries; with the W08/SM 12-entry list
        # bbb at slot 13 must also live in configJsonList for PitHouse to
        # resolve the slot to a known dirName.
        for fs_d in dashboards:
            fs_name = fs_d.get('dirName') or fs_d.get('name') or fs_d.get('title', '')
            if fs_name and fs_name not in canonical_list:
                canonical_list.append(fs_name)
    # imagePath: factory MD5/<hash>.png entries first (firmware-baked
    # ordering, captured), then FS user uploads.
    fs_img_path = list(image_path) if image_path is not None else []
    img_path: List[dict] = []
    if factory:
        for fact_p in factory.get('imagePath') or []:
            img_path.append(fact_p)
    seen_urls = {p.get('url') for p in img_path}
    for fs_p in fs_img_path:
        if fs_p.get('url') not in seen_urls:
            img_path.append(fs_p)
            seen_urls.add(fs_p.get('url'))
    # imageRefMap: real wheel emits THREE separate refmaps (verified via
    # diff_state_push.py against latestcaps capture):
    #   top-level `imageRefMap`         = sum of enable + disable refs
    #   `enableManager.imageRefMap`     = refs from enableManager dashes
    #   `disableManager.imageRefMap`    = refs from disableManager dashes
    # Earlier sim sent the same merged dict for all three. PitHouse may
    # cross-check the sums and reject when they disagree.
    enable_ref: Dict[str, int] = dict(factory.get('enableManager', {})
                                       .get('imageRefMap', {}) or {}) if factory else {}
    disable_ref: Dict[str, int] = dict(factory.get('disableManager', {})
                                        .get('imageRefMap', {}) or {}) if factory else {}
    # FS-derived refs (user uploads) live in enableManager only — once an
    # upload is on the wheel, it's "enabled". Add at refcount=1.
    fs_img_ref = dict(image_ref_map) if image_ref_map is not None else {}
    for fs_url, fs_count in fs_img_ref.items():
        if fs_url not in enable_ref:
            enable_ref[fs_url] = fs_count
    # Top-level = sum of enable + disable per real-wheel pattern.
    img_ref: Dict[str, int] = {}
    for k, v in enable_ref.items():
        img_ref[k] = img_ref.get(k, 0) + v
    for k, v in disable_ref.items():
        img_ref[k] = img_ref.get(k, 0) + v
    # Per real-wheel state push: disableManager.dashboards is a list of
    # disabled-dashboard records (we currently emit empty), enableManager
    # has its own refmap, top-level imageRefMap is the sum.
    disable_dashboards = list(factory.get('disableManager', {})
                                     .get('dashboards', []) or []) if factory else []
    state = {
        "TitleId": title_id,
        "configJsonList": canonical_list,
        "disableManager": {
            "dashboards": disable_dashboards,
            "imageRefMap": disable_ref,
            "rootPath": _WHEEL_FW_DASH_ROOT,
        },
        "displayVersion": display_version,
        "enableManager": {
            "dashboards": enable_dashboards,
            "imageRefMap": enable_ref,
            "rootPath": _WHEEL_FW_DASH_ROOT,
        },
        "fontRefMap": factory.get('fontRefMap') or {},
        "imagePath": img_path,
        "imageRefMap": img_ref,
        "resetVersion": reset_version,
        "rootDirPath": "/home/moza/resource",
        "sortTag": 0,
    }
    uncompressed = json.dumps(state, separators=(',', ':')).encode('utf-8')
    compressed = zlib.compress(uncompressed)
    envelope = (bytes([0x00])
                + struct.pack('<I', len(compressed))
                + struct.pack('<I', len(uncompressed))
                + compressed)
    return envelope


def build_configjson_state_schema_b(dashboards: List[dict],
                                    title_id: int = 4,
                                    factory_file: Optional[str] = None,
                                    image_path: Optional[list] = None,
                                    upload_in_progress: Optional[str] = None,
                                    deleted_ids: Optional[List[str]] = None) -> bytes:
    """Build a Schema B configJson delta + 9-byte envelope.

    Schema B is the post-2026-04 (KS Pro) firmware's lifecycle-event push.
    Emitted on session 0x0a after Schema A has already established the
    snapshot at connect. Body shape verified against
    `usb-capture/ksp/zlib/mozahubstartup/device_sess0x0a_b{200,201,202}_*.json`:

      { "TitleId": 4,
        "disabledManager": { "deletedDashboards": [],
                             "updateDashboards": [<entry>?] },
        "enabledManager":  { "deletedDashboards": [<id>?],
                             "updateDashboards": [<entry>+] },
        "imagePath": [<entry>+] }

    Lifecycle phases:
      - Mid-upload  → set `upload_in_progress=dirname`. That dashboard's
        record lands in `disabledManager.updateDashboards`; everything else
        empty.
      - Post-upload re-enumerate → leave `upload_in_progress=None`.
        Every entry in `dashboards` lands in `enabledManager.updateDashboards`.
      - Post-delete → also set `deleted_ids=[old_id]`. The id is whatever
        record was removed.
    """
    factory = _load_factory_configjson_state(factory_file)
    factory_by_name: Dict[str, dict] = {}
    for fs_file in (_FACTORY_STATE_FILE_W17_RGB, _FACTORY_STATE_FILE_W08_SM):
        alt = _load_factory_configjson_state(fs_file)
        for d in (alt.get('enableManager', {}).get('dashboards', []) or []):
            name = d.get('dirName') or d.get('title', '')
            if name and name not in factory_by_name:
                factory_by_name[name] = d
    for d in (factory.get('enableManager', {}).get('dashboards', []) or []):
        name = d.get('dirName') or d.get('title', '')
        if name:
            factory_by_name[name] = d

    default_ideal_device_infos: List[dict] = []
    for fd in (factory.get('enableManager', {}).get('dashboards', []) or []):
        if fd.get('idealDeviceInfos'):
            default_ideal_device_infos = fd['idealDeviceInfos']
            break

    import hashlib as _hashlib
    import time as _time
    import base64 as _b64

    def _materialize(name: str, fs_entry: Optional[dict]) -> dict:
        meta = factory_by_name.get(name)
        if meta:
            return meta
        raw_md5 = (fs_entry or {}).get('hash') or _hashlib.md5(
            name.encode()).hexdigest()
        encoded_hash = bytes(raw_md5, 'ascii').hex() if raw_md5 else ''
        id_seed = _hashlib.sha256(name.encode() + raw_md5.encode()).digest()
        upload_id = _b64.urlsafe_b64encode(id_seed)[:32].decode('ascii')
        upload_id = upload_id.replace('-', 'a').replace('_', 'b')
        last_mod_iso = _time.strftime('%Y-%m-%dT%H:%M:%SZ', _time.gmtime())
        preview_path = (f'{_WHEEL_FW_DASH_ROOT}/{name}/'
                        f'{name}.mzdash_v2_10_3_05.png')
        return {
            'createTime': '',
            'dirName': name,
            'hash': encoded_hash,
            'id': upload_id,
            'idealDeviceInfos': list(default_ideal_device_infos),
            'lastModified': last_mod_iso,
            'previewImageFilePaths': [preview_path],
            'resouceImageFilePaths': [],
            'title': (fs_entry or {}).get('title') or name,
        }

    fs_by_name = {(d.get('dirName') or d.get('name') or d.get('title', '')): d
                  for d in dashboards}

    disabled_update: List[dict] = []
    enabled_update: List[dict] = []
    if upload_in_progress:
        disabled_update.append(
            _materialize(upload_in_progress, fs_by_name.get(upload_in_progress)))
    else:
        for d in dashboards:
            name = d.get('dirName') or d.get('name') or d.get('title', '')
            if name:
                enabled_update.append(_materialize(name, d))

    img_path: List[dict] = []
    seen_urls: set = set()
    for fact_p in (factory.get('imagePath') or []):
        url = fact_p.get('url')
        if url and url not in seen_urls:
            img_path.append(fact_p)
            seen_urls.add(url)
    for fs_p in (image_path or []):
        url = fs_p.get('url')
        if url and url not in seen_urls:
            img_path.append(fs_p)
            seen_urls.add(url)

    state = {
        'TitleId': title_id,
        'disabledManager': {
            'deletedDashboards': [],
            'updateDashboards': disabled_update,
        },
        'enabledManager': {
            'deletedDashboards': list(deleted_ids) if deleted_ids else [],
            'updateDashboards': enabled_update,
        },
        'imagePath': img_path,
    }
    uncompressed = json.dumps(state, separators=(',', ':')).encode('utf-8')
    compressed = zlib.compress(uncompressed)
    return (bytes([0x00])
            + struct.pack('<I', len(compressed))
            + struct.pack('<I', len(uncompressed))
            + compressed)


def build_configjson_state_from_factory(factory_file: Optional[str] = None) -> bytes:
    """Serialize the real-wheel-captured factory configJson state verbatim
    (no FS merge, no per-dashboard augmentation). Used to replay the exact
    capture payload against PitHouse for intake testing."""
    factory = _load_factory_configjson_state(factory_file)
    if not factory:
        return b''
    uncompressed = json.dumps(factory, separators=(',', ':')).encode('utf-8')
    compressed = zlib.compress(uncompressed)
    return (bytes([0x00])
            + struct.pack('<I', len(compressed))
            + struct.pack('<I', len(uncompressed))
            + compressed)


def _synthesize_empty_fs_skeleton() -> List[dict]:
    """Return the persistent directory structure a factory-fresh MOZA wheel
    reports on session 0x04 when no dashboards are installed. Real firmware
    keeps `/home/root/resource/dashes/` as a persistent path regardless of
    whether any mzdash files live there; PitHouse needs this to know where
    uploads should land.
    """
    now_ms = int(time.time() * 1000)
    dashes = {
        'children': [], 'createTime': -28800000, 'fileSize': 0,
        'md5': '', 'modifyTime': now_ms, 'name': 'dashes',
    }
    resource = {
        'children': [dashes], 'createTime': -28800000, 'fileSize': 0,
        'md5': '', 'modifyTime': now_ms, 'name': 'resource',
    }
    moza = {
        'children': [resource], 'createTime': -28800000, 'fileSize': 0,
        'md5': '', 'modifyTime': now_ms, 'name': 'moza',
    }
    home = {
        'children': [moza], 'createTime': -28800000, 'fileSize': 0,
        'md5': '', 'modifyTime': now_ms, 'name': 'home',
    }
    return [home]


def build_session04_dir_listing(children: Optional[List[dict]] = None) -> bytes:
    """Build session 0x04 device→host root directory listing. Real wheel sends
    this shortly after session 0x04 opens to tell PitHouse what files already
    exist under /home/root. Schema from
    usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng:
      {children:[{children, createTime, fileSize, md5, modifyTime, name}],
       createTime, fileSize, md5, modifyTime, name:"root"}

    53-byte pre-zlib prefix (bytes copied verbatim from capture where field
    semantics are unclear):
        0a                               (1)  subtype tag
        <size LE:4>                      (4)  bytes after this field
        00 00 00 <pathlen_BE:1> 00       (5)  path-length marker
        <UTF-16LE path>                 (20)  "/home/root"
        ff ff ff ff ff ff ff ff 00       (9)  padding sentinel
        de c3 90 00 00 00 00 00 00 00   (10)  unknown metadata block
        a9 88 01 00                      (4)  unknown (LE 100521 — not uncomp size)
    Followed by the zlib deflate stream of the JSON listing. Previous 9-byte
    [flag][comp_LE][uncomp_LE] envelope didn't match real-wheel wire format
    and caused PitHouse to skip FS-state acknowledgement."""
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

    path_utf16 = '/home/root'.encode('utf-16-le')       # 20B
    path_field = struct.pack('>I', len(path_utf16)) + b'\x00'  # 5B
    padding = b'\xff' * 8 + b'\x00'                     # 9B
    metadata = (b'\xde\xc3\x90' + b'\x00' * 7           # 10B
                + b'\xa9\x88\x01\x00')                  # 4B
    body = (path_field
            + path_utf16
            + padding
            + metadata
            + compressed)
    envelope = b'\x0a' + struct.pack('<I', len(body))
    return envelope + body


def chunk_session_payload(session: int, start_seq: int, payload: bytes,
                          chunk_size: int = 54,
                          crc_bytes: int = 4) -> List[bytes]:
    """Split `payload` into per-chunk 7c:00 session-data frames with CRC32
    trailer. `crc_bytes` selects:
      - 4: full CRC32-LE. What PitHouse's handshake/catalog/configJson paths
        accept (tested against a user PitHouse 2026-04 build). Default.
      - 3: 3-byte truncated CRC32-LE (first 3 bytes). What real wheel firmware
        emits on every session (verified in all captures 2026-04-24) and
        what PitHouse requires for the file-transfer reply path — 4 bytes
        there compounds into a 3-byte offset shift that silently stalls
        uploads at the content phase.

    Empirically the handshake paths tolerate the extra byte (it lands in a
    tolerated-padding region); the file-transfer reply path does not. Keeping
    the 4-byte default preserves working handshake behaviour; callers who
    emit wheel-wire-exact bytes (sub-msg 1/2 acks) pass `crc_bytes=3`."""
    if crc_bytes not in (0, 3, 4):
        raise ValueError('crc_bytes must be 0, 3, or 4')
    frames = []
    seq = start_seq
    for off in range(0, len(payload), chunk_size):
        net = payload[off:off + chunk_size]
        if crc_bytes:
            crc = struct.pack('<I', zlib.crc32(net))[:crc_bytes]
            chunk = net + crc
        else:
            chunk = net
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
        # Offset within each session buffer where the previous decoded blob's
        # zlib stream ended. Used to compute `envelope_from_prev_hex` — the
        # bytes between two successive blobs, which carry the per-blob framing
        # envelope (length, sequence, sentinels) we're trying to reverse.
        self._prev_blob_end: Dict[int, int] = {}

    def feed(self, session: int, chunk: bytes) -> Optional[dict]:
        """Append `chunk` to session buffer and return any newly-decoded blob.
        Handles CRC-aware trailer strip so callers can pass raw chunk payload
        (including the 4-byte CRC32) without pre-processing — the CRC would
        otherwise interleave with content and corrupt UTF-16LE path decoding
        and zlib stream reassembly across chunks.

        Every chunk carries a 4-byte CRC32-LE trailer (verified against raw
        tshark output of all captures 2026-04-24). The (b)-format variant
        has a leading flag byte before the net data, also followed by a 4-byte
        CRC."""
        self._log_chunk(session, chunk)
        buf = self._bufs.setdefault(session, bytearray())
        if len(chunk) >= 4:
            crc_wire = int.from_bytes(chunk[-4:], 'little')
            if zlib.crc32(bytes(chunk[:-4])) == crc_wire:
                chunk = chunk[:-4]
            elif len(chunk) >= 5 and zlib.crc32(bytes(chunk[1:-4])) == crc_wire:
                chunk = chunk[1:-4]
        buf.extend(chunk)
        return self._scan(session, buf)

    def _log_chunk(self, session: int, chunk: bytes) -> None:
        """Append raw chunk bytes (before CRC strip) to per-session dump files.
        Raw log keeps every byte PitHouse sent, not only the decoded-blob
        candidates — useful when protocol doesn't match zlib magic."""
        try:
            out_dir = _LOG_DIR / 'uploads'
            out_dir.mkdir(parents=True, exist_ok=True)
            with open(out_dir / f'sess{session:02x}_raw.bin', 'ab') as f:
                f.write(chunk)
            with open(out_dir / f'sess{session:02x}_frames.log', 'a') as f:
                f.write(f'{time.strftime("%H:%M:%S")} {len(chunk):4d} {chunk.hex(" ")}\n')
        except Exception as _e:
            _swallow('chunk_log_write', _e)

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
            # S1 research: capture up to 64 bytes before the zlib magic. Two
            # views to help envelope RE:
            #   envelope_hex         — bytes immediately before zlib magic
            #   envelope_from_prev_hex — bytes between previous blob's end and
            #     this blob's zlib magic (the per-blob framing envelope)
            env_start = max(0, off - 64)
            envelope_hex = bytes(buf[env_start:off]).hex()
            prev_end = self._prev_blob_end.get(session, 0)
            envelope_from_prev_hex = bytes(buf[prev_end:off]).hex() if prev_end else envelope_hex
            # Compressed-stream end offset = start + bytes dobj consumed.
            # decompressobj exposes unused_data after eof; the consumed length
            # is len(buf[off:]) - len(unused_data).
            consumed = len(buf) - off - len(dobj.unused_data)
            blob_end = off + consumed
            blob = {'session': session, 'session_offset': off, 'size': len(decomp),
                    'raw': decomp,
                    'envelope_hex': envelope_hex,
                    'envelope_from_prev_hex': envelope_from_prev_hex,
                    'envelope_from_prev_len': len(envelope_from_prev_hex) // 2,
                    'compressed_size': consumed,
                    'compressed_end': blob_end}
            self._prev_blob_end[session] = blob_end
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
            # S3 research: if this is a tile-server state blob, parse out the
            # ATS/ETS2 inner JSON + populated-vs-empty flags.
            self._extract_tile_server_fields(blob)
            self.decoded_blobs.append(blob)
            self._dump_blob_to_disk(blob)
            self._extract_dashboard_metadata(blob)
            return blob
        return None

    def _extract_tile_server_fields(self, blob: dict) -> None:
        """Surface tile-server structure on session 0x03 blobs. PitHouse wraps
        each game's state as an escaped JSON string under `map.ats`/`map.ets2`;
        parse those strings and classify empty vs populated."""
        j = blob.get('json')
        if not isinstance(j, dict) or 'map' not in j:
            return
        m = j.get('map')
        if not isinstance(m, dict):
            return
        out: Dict[str, dict] = {}
        for game in ('ats', 'ets2'):
            raw = m.get(game)
            if not isinstance(raw, str):
                continue
            try:
                inner = json.loads(raw)
            except (TypeError, json.JSONDecodeError):
                continue
            layers = inner.get('layers') or []
            levels = inner.get('levels') or {}
            out[game] = {
                'populated': bool(layers) and inner.get('map_version', -1) != -1,
                'name': inner.get('name', ''),
                'bg': inner.get('bg', ''),
                'file_type': inner.get('file_type', ''),
                'map_version': inner.get('map_version', -1),
                'version': inner.get('version', 0),
                'tile_size': inner.get('tile_size', 0),
                'pm_support': inner.get('pm_support', False),
                'pmtiles_exists': inner.get('pmtiles_exists', False),
                'root': inner.get('root', ''),
                'layers_count': len(layers) if isinstance(layers, list) else 0,
                'levels_count': len(levels) if isinstance(levels, dict) else 0,
                'ext_files': inner.get('ext_files', []),
                'bounds': {
                    'x_min': inner.get('x_min', 0), 'x_max': inner.get('x_max', 0),
                    'y_min': inner.get('y_min', 0), 'y_max': inner.get('y_max', 0),
                },
            }
        if out:
            blob['tile_server'] = {
                'games': out,
                'root': j.get('root', ''),
                'version': j.get('version', 0),
                'any_populated': any(v['populated'] for v in out.values()),
            }

    def _dump_blob_to_disk(self, blob: dict) -> None:
        """Persist decoded blob to sim/logs/uploads/ for offline inspection."""
        try:
            out_dir = _LOG_DIR / 'uploads'
            out_dir.mkdir(parents=True, exist_ok=True)
            stem = f"sess{blob['session']:02x}_off{blob['session_offset']:05x}_sz{blob['size']}"
            (out_dir / f'{stem}.bin').write_bytes(blob.get('raw', b''))
            if isinstance(blob.get('json'), (dict, list)):
                (out_dir / f'{stem}.json').write_text(
                    json.dumps(blob['json'], indent=2, ensure_ascii=False))
            elif blob.get('utf16'):
                (out_dir / f'{stem}.utf16.txt').write_text(
                    blob['utf16'], encoding='utf-8', errors='replace')
        except Exception as _e:
            _swallow('blob_dump', _e)

    def _extract_dashboard_metadata(self, blob: dict) -> None:
        """If `blob` JSON looks like a mzdash file or a configJson state,
        capture dashboard name + hash. mzdash files don't have a top-level
        `name`; dashboard name is carried in the session 0x04 file transfer
        path (`/home/root/resource/dashes/<name>/<name>.mzdash`), decoded by
        feed() from the same session's UTF-16LE chunks and attached below."""
        j = blob.get('json')
        # RPC-shape detection: dict with an `id` field + a key matching
        # `<name>()` pattern is a wheel-device JSON RPC. Log it so the sim
        # (and MCP tooling) can inspect what PitHouse is asking for.
        # Regex accepts empty method name — the reset RPC uses literal `()`
        # with no prefix. Without `*`, those blobs never made it to rpc_log.
        if isinstance(j, dict) and 'id' in j:
            import re as _re
            for k in j.keys():
                if isinstance(k, str) and _re.match(r'^[A-Za-z_0-9]*\(\)$', k):
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
        """Scan session buffer for the dashboard destination path (UTF-16LE).

        Path shapes observed:
        - Old firmware: `/home/root/resource/dashes/<name>/<name>.mzdash`
          (in the host type=0x84 remote TLV on session 0x04).
        - New firmware (2026+) remote TLV: `/home/root/resource/dashes/<name>.mzdash`
          (in the host type=0x03 0x70 TLV on session 0x06).
        - 2026-04 PitHouse: no remote path is sent at all. Only the
          PitHouse-local stage path appears, of the shape
          `<...>/MOZA Pit House/_dashes/<hash>/dashes/<name>/<name>.mzdash`
          (sep is `/` or `\\`). Extract <name> from there as a fallback.
        """
        buf = self._bufs.get(session)
        if not buf:
            return None
        try:
            text = bytes(buf).decode('utf-16-le', errors='ignore')
        except Exception:
            return None
        import re as _re
        # Real wheel rootDirPath = /home/moza/resource (verified
        # usb-capture/payload-09-state-re.md). Older firmware/captures used
        # /home/root — keep both regex variants for back-compat with old
        # PitHouse builds that may still emit the legacy remote path.
        m = _re.search(r'/home/(?:moza|root)/resource/dashes/([^/]+)/\1\.mzdash', text)
        if m:
            return m.group(1)
        m = _re.search(r'/home/(?:moza|root)/resource/dashes/([^/]+?)\.mzdash', text)
        if m:
            return m.group(1)
        m = _re.search(r'[/\\]_dashes[/\\][^/\\]+[/\\]dashes[/\\]([^/\\]+)[/\\]\1\.mzdash', text)
        if m:
            return m.group(1)
        return None


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
        time.sleep(_DASH_REPLY_POLL_SEC)
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
                time.sleep(_DASH_REPLY_FRAME_SPACING_SEC)

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

    def load_json(self, path: str) -> int:
        """Load request/response pairs from a JSON replay table. Returns added count.

        Schema v1 format (one file per device, groups embedded in entry keys):
            {"schema": 1,
             "device": <int>,  # target device byte (e.g. 0x17 = wheel)
             "label": "<optional description>",
             "source": "<optional: origin capture path>",
             "entries": {
                 "<group_hex>:<req_payload_hex>": "<rsp_frame_hex>",
                 ...
             }}

        Group is the host-side request group byte. `req_payload_hex` is the
        frame payload (cmd + data, no grp/dev/cksum). `rsp_frame_hex` is the
        full wheel→host wire frame (7e N grp dev payload cksum). First entry
        per (group, device, payload) key wins across sources.
        """
        with open(path) as fh:
            data = json.load(fh)
        if data.get('schema') != 1:
            raise ValueError(f'{path}: unsupported schema (expected 1)')
        req_device = int(data['device'])
        added = 0
        for key_str, rsp_hex in data.get('entries', {}).items():
            try:
                grp_str, payload_str = key_str.split(':', 1)
            except ValueError:
                continue
            req_group = int(grp_str, 16)
            req_pl = bytes.fromhex(payload_str)
            rsp_frame = bytes.fromhex(rsp_hex)
            if not verify(rsp_frame):
                continue
            key = (req_group, req_device, req_pl)
            if key not in self._table:
                self._table[key] = rsp_frame
                self._by_device[(req_group, req_device)] = \
                    self._by_device.get((req_group, req_device), 0) + 1
                added += 1
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
                 device_id_rsp: Optional[Dict] = None,
                 display_model_name: str = '',
                 rpm_led_count: int = _DEFAULT_RPM_LED_COUNT,
                 button_led_count: int = _DEFAULT_BUTTON_LED_COUNT,
                 wheel_device: Optional[int] = None,
                 factory_state_file: Optional[str] = None,
                 proactive_session09: bool = True,
                 configjson_session: int = 0x09):
        self._db = db
        self._replay = replay
        # Path (relative to sim/) of captured factory configJson state for
        # this wheel model. Drives session 0x09 state push contents and
        # initial FS seeding so the sim looks like real out-of-box hardware.
        self._factory_state_file = factory_state_file
        # Whether to push configJson state proactively in the device-init
        # burst. All current models need this for dashboard detection;
        # parameter retained per-model in case a future firmware deprecates
        # the proactive push.
        self._proactive_session09 = proactive_session09
        # Session number the wheel pushes configJson state on. Older
        # firmware (VGS/CSP, 2025-11) uses 0x09; KS Pro / 2026-04+ firmware
        # moved it to 0x0a (session 0x09 became empty heartbeats only;
        # tile-server host→dev push moved from 0x03 to 0x04, and the wheel
        # gained a dev→host tile-server mirror on 0x0b). PitHouse parses
        # state push + dashboard-list reply on whichever session the wheel
        # pushes on.
        self._configjson_session = configjson_session
        self._plugin_probe_rsp = plugin_probe_rsp if plugin_probe_rsp is not None else _PLUGIN_PROBE_RSP
        self._pithouse_id_rsp = pithouse_id_rsp if pithouse_id_rsp is not None else _PITHOUSE_ID_RSP
        self._device_id_rsp = device_id_rsp if device_id_rsp is not None else _DEVICE_ID_RSP
        # Group 0x0E parameter-table replay (per-device map of request_hex →
        # response_hex extracted from real captures). Loaded lazily; entries
        # for hub/base/wheel cover ~170 unique params PitHouse polls on
        # connect. See sim/replay/param_0e_dev{12,13,17}.json.
        self._param_0e_tables: Dict[int, Dict[str, str]] = {}
        for dev_addr in (0x12, 0x13, 0x17):
            p = Path(__file__).parent / 'replay' / f'param_0e_dev{dev_addr:02x}.json'
            if p.exists():
                try:
                    self._param_0e_tables[dev_addr] = json.loads(p.read_text()).get('entries', {})
                except Exception:
                    pass
        self._display_model_name = display_model_name or _DISPLAY_MODEL_NAME
        # Wheel device address: 0x17 for new-protocol wheels, 0x13 for ES.
        # Drives all wheel-routed dispatch + simulated-device set membership.
        self.wheel_device = wheel_device if wheel_device is not None else _WHEEL_DEVICE
        self.wheel_device_rsp = swap_nibbles(self.wheel_device)
        # ES suppresses any response at 0x17 — real ES wheels don't enumerate
        # there. {0x12 hub, 0x13 base} for ES; {0x12, 0x13, 0x17} otherwise.
        # Pedal 0x19 joins the set when the model has pedal_identity so
        # heartbeat/keepalive ACKs don't drop silently.
        self._simulated_devices = {0x12, 0x13, self.wheel_device}
        if any(dev == 0x19 for (dev, _, _) in self._device_id_rsp):
            self._simulated_devices.add(0x19)
        self.mgmt_session = 0
        self.telem_session = 0
        self.sessions_opened = 0
        self._reconnect_detected = False
        self.tier_def_received = False
        self.display_detected = False
        self.tiers: Dict[int, List[dict]] = {}  # flag_byte → channels
        self.channels: List[dict] = []           # all channels merged (for display)
        self.values: Dict[str, float] = {}
        self.rpm_led_mask: int = 0
        self.button_led_mask: int = 0
        self.rpm_led_count: int = rpm_led_count
        self.button_led_count: int = button_led_count
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
        self.recent_frames: Deque[Tuple[str, str]] = collections.deque(maxlen=_RECENT_FRAME_RING_SIZE)
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
        # Device-side seq counter for file-transfer replies. Independent
        # from the host's seq on the same session (7c:00 uses per-direction
        # counters). Real wheel starts device-side seq at session-port + 1
        # (e.g. port=6 → first data at seq 0x07); earlier sim code reused
        # `_upload_next_seq` which tracks the host side and desynced PitHouse.
        self._ft_reply_next_seq: Dict[int, int] = {}
        # Per-session count of "type=0x03 rounds" we've sent a progress ack
        # for. -1 = no first ack yet. 0xFFFF = upload finalized.
        self._ft_rounds_acked: Dict[int, int] = {}
        # Session 0x04 file-transfer per-sub-msg ack state. Tracks how many
        # sub-msg responses we've emitted (1 after sub-msg 1, 2 after sub-msg 2)
        # and the running byte count for bytes_written.
        self._ft_echo_timer = None  # type: Optional[threading.Timer]
        self._device_init_timer = None  # type: Optional[threading.Timer]
        self._ft_submsg_emitted = 0
        self._ft_received_bytes = 0
        self._pending_lock = threading.Lock()
        # Gate the canned dash-reply replay. Real upload traffic arrives on
        # session 0x04 FF-prefixed chunks; firing the recorded "stored dash"
        # reply mid-upload tricks PitHouse into aborting the real transfer.
        # Default: off. Re-enable per-session only after a full upload parses
        # successfully via _parse_upload.
        self.dash_reply_enabled = False
        # RPC replies: track which rpc_log entries we've already responded to
        # and per-session outbound seq counters for our reply frames.
        self._rpc_replied_index = 0
        self._rpc_seq: Dict[int, int] = {}
        # PitHouse-assigned dashboard IDs. Populated from any host→wheel
        # configJson reply the sim observes (session 0x09 inbound) so that
        # `completelyRemove(id)` can resolve back to a dirName even when
        # PitHouse's id doesn't match the sim's synthesized `sim-<md5>-<name>`.
        self._pithouse_dashboard_ids: Dict[str, str] = {}
        # Schema B (KS Pro) post-delete deltas need to surface the deleted
        # dashboard's id under `enabledManager.deletedDashboards`. Buffer the
        # ids of recently-removed entries here; the next state-refresh drains
        # the buffer into the next emitted Schema B body.
        self._pending_deleted_ids: List[str] = []
        # Per-session dirname currently being uploaded. Set when chunk-0's
        # bundle file table becomes parseable (Schema B Phase 1 fire);
        # cleared at upload-completion (Schema B Phase 2 fire). Prevents
        # re-firing Phase 1 on every subsequent chunk while one upload is
        # in progress.
        self._mid_upload_dirname: Dict[int, str] = {}
        # Decode PitHouse uploads inline. Exposes what was pushed (dashboards,
        # channel catalog, tile-map config) so the sim can echo matching state
        # back to PitHouse (dashboard list / active-selection confirmation).
        self._upload_tracker = UploadTracker()
        # configJson cache-version stamps. PitHouse uses these to decide when
        # to invalidate its cached wheel-state. Without bumping on FS
        # mutation, PitHouse keeps showing the pre-mutation dashboard list
        # even after the wheel emits a fresh session 0x09 state push.
        # Persisted across sim restarts so each reconnect reports a value
        # ≥ what PitHouse last saw — otherwise PitHouse re-loads the same
        # cached state every reload (versions reset to factory < cached).
        _factory = _load_factory_configjson_state(factory_state_file)
        self._versions_path = _LOG_DIR / 'configjson_versions.json'
        try:
            v = json.loads(self._versions_path.read_text())
            self._display_version = int(v.get('displayVersion',
                _factory.get('displayVersion', 11)))
            self._reset_version = int(v.get('resetVersion',
                _factory.get('resetVersion', 10)))
        except Exception:
            self._display_version = int(_factory.get('displayVersion', 11))
            self._reset_version = int(_factory.get('resetVersion', 10))
        # Per-session SerialStream 7c:00 data-chunk counts keyed by session id.
        # Research tool: lets us see which session carries how much traffic
        # (e.g. session 0x02 tier-def + dictionary vs session 0x03 tile-server).
        self.session_data_counts: Dict[int, int] = {}
        # Device-initiated sessions we've queued opens for. Real wheel opens
        # 0x04/0x06/0x08/0x09/0x0a after the host brings up 0x01/0x02 — PitHouse
        # waits for the device's session 0x09 open before asking for the
        # dashboard list, so without these the Dashboard Manager UI stays empty.
        self.device_opened_sessions: Dict[int, int] = {}  # session → port
        self._device_init_started = False
        # Active-dashboard tracking. PitHouse reads `28:00` periodically to
        # learn which dash slot the wheel currently displays, and `28:01`
        # for that dash's page count. Real wheel persists this across
        # disconnects. Sim defaults to slot 1 / 1 page. Mutated via
        # `set_active_dashboard()` (also exposed as MCP tool).
        # NOTE: the SET-side of this signal hasn't been observed in any
        # capture — PitHouse never writes 3F:28 in the dash-change captures.
        # Active-dash switching may instead route through session 0x0a RPC
        # (e.g. `useDashboard()`) — Phase-3 follow-up. Until then sim only
        # reflects the value when set externally via the MCP tool.
        self.active_dash_index = 1
        self.active_dash_pages = 1
        # Virtual wheel filesystem — tracks ONLY user-installed content
        # (uploaded dashboards, tile server caches). Persisted across
        # restarts at sim/logs/wheel_fs.json. Starts empty.
        #
        # Factory dashboards are NOT stored in this FS. They live in the
        # captured `factory_state_<...>.json` (firmware ROM analog) and are
        # merged into the session 0x09 state push by build_configjson_state.
        # Real wheel does the same: dashboards in ROM aren't queryable as
        # files (`/home/root` listing returns just `temp/`); the wheel
        # advertises them only via the configJson state blob.
        self.fs = WheelFileSystem(
            persist_path=_LOG_DIR / 'wheel_fs.json')
        # Purge any legacy /home/root/resource/dashes/* entries left over
        # from sim builds before 2026-04-25 (path-layout migration). Safe
        # because nothing in current sim references that prefix.
        self.fs.purge_legacy_dash_paths()

    @property
    def stored_dashboards(self) -> List[dict]:
        """Derived from wheel filesystem. Never a cached list — always reads
        live from WheelFileSystem so mutations take effect immediately."""
        return self.fs.dashboards()

    def list_advertised_dashboards(self) -> List[dict]:
        """All dashboards the sim advertises in session 0x09 state — factory
        ROM entries first, then FS-tracked user uploads. Same order as
        `enableManager.dashboards[]` in the state push.
        """
        factory = _load_factory_configjson_state(self._factory_state_file)
        out = list(factory.get('enableManager', {}).get('dashboards', []) or [])
        out_names = {(d.get('dirName') or d.get('title')) for d in out}
        for d in self.fs.dashboards():
            name = d.get('dirName') or d.get('name') or d.get('title')
            if name and name not in out_names:
                out.append(d)
                out_names.add(name)
        return out

    def set_active_dashboard(self, target, pages: int = 1) -> dict:
        """Track which dashboard the wheel "displays". `target` may be an
        integer slot index (1-N), a dirName, or a dashboard id. Updates the
        28:00/28:01 reply values + pushes a fresh session 0x09 state blob.

        Returns `{index, dirName, pages}`. Raises ValueError if `target`
        doesn't resolve to a known dashboard.

        NOTE: not yet wire-driven — PitHouse's set-side signal isn't fully
        RE'd. Use this for sim diagnostics until Phase 3 follow-up resolves
        the SET path (likely session 0x0a RPC `useDashboard()` or similar).
        """
        advertised = self.list_advertised_dashboards()
        idx = None
        match = None
        if isinstance(target, int):
            if 1 <= target <= len(advertised):
                idx = target
                match = advertised[idx - 1]
        else:
            tgt = str(target)
            for i, d in enumerate(advertised, start=1):
                if (d.get('dirName') == tgt or d.get('title') == tgt
                        or d.get('id') == tgt):
                    idx = i
                    match = d
                    break
        if idx is None or match is None:
            raise ValueError(f'unknown dashboard: {target!r}; '
                             f'advertised: {[d.get("dirName") for d in advertised]}')
        self.active_dash_index = idx
        self.active_dash_pages = max(1, int(pages))
        # Push a fresh 0x09 state so any UI side reading wheel state via
        # session 0x09 picks up the change immediately. The blob itself
        # doesn't encode active-dash, but a re-push triggers PitHouse to
        # re-read 28:00 in some flows.
        try:
            self._fire_state_refresh()
        except AttributeError:
            pass
        return {
            'index': idx,
            'dirName': match.get('dirName') or match.get('title'),
            'pages': self.active_dash_pages,
        }

    def _record(self, tag: str, frame: bytes) -> None:
        """Central bookkeeping: bump per-tag count, update last-frame + recent log."""
        self.cat_counts[tag] = self.cat_counts.get(tag, 0) + 1
        hx = frame.hex(' ')
        self.last_frame_hex = hx
        self.last_handler_tag = tag
        self.recent_frames.appendleft((tag, hx))

    def _reset_connection_state(self) -> None:
        """Clear per-connection state so a fresh host handshake starts clean
        after a SimHub/plugin restart. Preserves wheel-level identity, the
        persistent filesystem, cumulative frame counters, and recent-log
        ring so diagnostics survive the reconnect."""
        if self.emitter:
            self.emitter.emit_event('reconnect')
        self.sessions_opened = 0
        self.mgmt_session = 0
        self.telem_session = 0
        self._reconnect_detected = False
        self.tier_def_received = False
        self.tiers = {}
        self.channels = []
        self._bufs = {}
        self.session_open_seqs = {}
        self._device_init_started = False
        self._configjson_state_pushed = False
        self._peer_session_acks = {}
        self.device_opened_sessions = {}
        # Cancel pending keepalive timer — `_tick_session09_keepalive`
        # re-arms based on `_device_init_started`, but a Timer scheduled
        # before this reset will fire once before noticing. Explicit cancel
        # avoids a stray frame queued onto the just-cleared `_pending_sends`.
        ka_timer = getattr(self, '_session09_ka_timer', None)
        if ka_timer is not None:
            try:
                ka_timer.cancel()
            except Exception:
                pass
            self._session09_ka_timer = None
        self._upload_last_ff_ts = {}
        self._upload_replied = set()
        self._upload_next_seq = {}
        # Guard reset against a Timer firing concurrently — without the lock a
        # late callback could extend() the now-detached list and silently drop
        # those frames from the live `_pending_sends`.
        with self._pending_lock:
            self._pending_sends = []
        if self._upload_reply_timer is not None:
            try:
                self._upload_reply_timer.cancel()
            except Exception:
                pass
            self._upload_reply_timer = None
        if self._ft_echo_timer is not None:
            try:
                self._ft_echo_timer.cancel()
            except Exception:
                pass
            self._ft_echo_timer = None
        if self._device_init_timer is not None:
            try:
                self._device_init_timer.cancel()
            except Exception:
                pass
            self._device_init_timer = None
        self._ft_submsg_emitted = 0
        self._ft_received_bytes = 0
        self.catalog_sent = False
        self.session_data_counts = {}

    def _fire_device_init(self) -> None:
        """Queue device-initiated session opens (0x04/0x06/0x08/0x09/0x0a) and
        the initial configJson state push. Runs ~150ms after the host has
        opened its sessions — both at sim startup AND on PitHouse re-handshake
        (host-side reset triggers `_reset_connection_state` which clears
        `_device_init_started`, so this re-fires). Frames accumulate in
        `_pending_sends` and get flushed piggybacked on the next handle()
        return path."""
        frames: List[bytes] = []
        for sess, port, _ in _DEVICE_SESSIONS:
            frames.append(resp_device_session_open(sess, port))
            self.device_opened_sessions[sess] = port
        # Channel catalog (sessions 0x01 + 0x02). Without this PitHouse keeps
        # its cached tier_def from the prior handshake and skips re-pushing
        # tier_def on reconnect — which gates display_cfg, which gates
        # uploads. The proactive_sender thread only emits the catalog ONCE at
        # sim startup, so reconnects need to re-emit it here.
        for sess_id in sorted(self._device_catalog.keys()):
            if sess_id in (0x01, 0x02):
                frames.extend(self._device_catalog[sess_id])
        self.catalog_sent = True
        # Session 0x09 configJson state push. Cadence is firmware-version
        # dependent: VGS/CSP firmware pushes state proactively in the
        # device-init burst; KS Pro 2026-04 firmware (verified against
        # putOnWheelAndOpenPitHouse.pcapng — device opens 0x09 but never
        # pushes state in the captured window) does not. Sim mirrors this
        # via the model's `proactive_session09` flag (default True for
        # backward compat with VGS/CSP). When False, the seq counter is
        # still reserved so a later `_fire_state_refresh` trigger can push
        # fresh state once an FS mutation or RPC asks for it.
        # Session number is firmware-dependent: 0x09 for older (VGS/CSP)
        # firmware, 0x0a for KS Pro / 2026-04+. Push state in the device-
        # init burst so PitHouse has it on hand; FC:00 handler also
        # re-pushes on first host fc:00 ack as a backup for cases where
        # the device-init push lands before PitHouse marks the session
        # ready.
        if self._proactive_session09:
            _img_ref, _img_path = self.fs.image_manifest()
            state = build_configjson_state(self.fs.dashboards(),
                                           display_version=self._display_version,
                                           reset_version=self._reset_version,
                                           factory_file=self._factory_state_file,
                                           image_ref_map=_img_ref,
                                           image_path=_img_path,
                                           display_product_type=self._display_model_name)
            sess = self._configjson_session
            if sess == 0x0a:
                start_seq = 0x000b
                crc_bytes = 0
            else:
                start_seq = 0x000a
                crc_bytes = 4
            frames.extend(chunk_session_payload(
                sess, start_seq, state, crc_bytes=crc_bytes))
            self._session09_next_seq = (
                start_seq + max(1, (len(state) + 53) // 54)
            ) & 0xFFFF
        elif self._configjson_session == 0x0a:
            self._session09_next_seq = 0x000b
        else:
            self._session09_next_seq = 0x000a
        # Root filesystem listing on session 0x04 — PitHouse uses this to
        # enumerate what's already on the wheel before a fresh upload.
        # Proactive emit here was in an invented format (zlib-JSON "temp"
        # dummy entry) that PitHouse didn't recognise, so disabling. Real
        # firmware sends dir-listings REACTIVELY in response to host type=0x08
        # probes — handled in the session 0x04 data path via
        # `build_dir_listing_reply`. Seq starts at 1 (real wheel uses low
        # sequential seqs per direction; sim's earlier 0x0100 was made up and
        # caused PitHouse to ignore replies).
        self._session04_next_seq = 0x0001
        with self._pending_lock:
            self._pending_sends.extend(frames)
        self.cat_counts['device_init'] = self.cat_counts.get('device_init', 0) + len(frames)
        if self.emitter:
            self.emitter.emit_event('device_init',
                sessions=sorted(f'0x{s:02x}' for s in self.device_opened_sessions),
                dashboards=len(self.stored_dashboards),
                frames=len(frames))
        # Start session 0x09 keepalive heartbeat. Real wheel emits zero-payload
        # session 0x09 chunks every ~2s after the initial state push (verified
        # in latestcaps post-state-push tail: 16 contiguous keepalives at 2s
        # cadence). Without these, PitHouse may treat session 0x09 as stalled.
        self._schedule_session09_keepalive()

    def _schedule_session09_keepalive(self) -> None:
        timer = threading.Timer(_SESSION09_KEEPALIVE_SEC, self._tick_session09_keepalive)
        timer.daemon = True
        timer.start()
        self._session09_ka_timer = timer

    def _tick_session09_keepalive(self) -> None:
        try:
            self._emit_session09_keepalive()
        except Exception as _e:
            _swallow('session09_keepalive', _e, sim=self)
        # Re-arm only while sim is running and session 0x09 is alive.
        if self._device_init_started:
            self._schedule_session09_keepalive()

    def push_configjson_replay(self, use_factory: bool = True) -> int:
        """Queue a session 0x09 configJson state push on demand. Returns the
        number of frames queued. If `use_factory`, serializes the captured
        real-wheel factory state verbatim (no FS merge); otherwise uses the
        sim-built state derived from current FS (same as `_fire_state_refresh`
        but without the session 0x04 dir listing)."""
        if use_factory:
            state = build_configjson_state_from_factory(self._factory_state_file)
        else:
            _img_ref, _img_path = self.fs.image_manifest()
            state = build_configjson_state(self.fs.dashboards(),
                                           display_version=self._display_version,
                                           reset_version=self._reset_version,
                                           factory_file=self._factory_state_file,
                                           image_ref_map=_img_ref,
                                           image_path=_img_path,
                                           display_product_type=self._display_model_name)
        if not state:
            return 0
        seq09 = getattr(self, '_session09_next_seq', 0x0200)
        frames = chunk_session_payload(
            self._configjson_session, seq09, state,
            crc_bytes=(0 if self._configjson_session == 0x0a else 4))
        self._session09_next_seq = (seq09 + max(1, (len(state) + 53) // 54)) & 0xFFFF
        with self._pending_lock:
            self._pending_sends.extend(frames)
        self.cat_counts['configjson_replay'] = self.cat_counts.get('configjson_replay', 0) + len(frames)
        if self.emitter:
            self.emitter.emit_event('configjson_replay',
                use_factory=use_factory, state_bytes=len(state), frames=len(frames))
        return len(frames)

    def _fire_state_refresh(self) -> None:
        """Re-push configJson state (session 0x09) after a FS mutation
        (upload, delete). PitHouse Dashboard Manager picks up the new state
        without a full reconnect. Dir-listing (session 0x04) is reactive-only
        now — PitHouse re-probes after the configJson update if it wants the
        new directory view; we answer via `build_dir_listing_reply`.

        Bumps `displayVersion` to invalidate PitHouse's cache. Without this
        bump PitHouse keeps showing pre-mutation dashboard list even though
        the wire-correct state push fired.

        Debounced to 500ms — bursts of FS mutations (e.g. multi-file upload
        bundle that writes 6 files in quick succession) would otherwise fire
        6 state pushes back-to-back and saturate the bus during PitHouse's
        own file-transfer ack flow."""
        now = time.monotonic()
        last = getattr(self, '_state_refresh_last_ts', 0.0)
        if now - last < _STATE_REFRESH_DEBOUNCE_SEC:
            self._state_refresh_pending = True
            return
        self._state_refresh_last_ts = now
        self._state_refresh_pending = False
        # Don't bump displayVersion. Verified 2026-04-25 against multiple
        # captures of the same firmware: displayVersion + resetVersion are
        # firmware-stamped CONSTANTS (W17 RGB = 11/10, W08 SM = 5/5),
        # identical across hours-apart sessions on the same wheel. Earlier
        # "auto-bump on every refresh" assumption pushed sim values to 50+
        # which likely caused PitHouse to reject the state push as
        # incompatible-firmware-version. Values now stay at the captured
        # factory baseline.
        self._save_versions()
        _img_ref, _img_path = self.fs.image_manifest()
        # KS Pro / 2026-04+ firmware: state-refresh emits a Schema B
        # post-upload re-enumerate delta on session 0x0a (verified against
        # device_sess0x0a_b202 in usb-capture/ksp/zlib/mozahubstartup —
        # `enabledManager.updateDashboards` carries the full current list,
        # `disabledManager` is empty, `imagePath` mirrors the FS image
        # manifest). Real wheel emits Schema A only at first connect, then
        # Schema B for every subsequent change. Older firmware (0x09) keeps
        # the Schema A snapshot path because Schema B isn't on the wire there.
        if self._configjson_session == 0x0a:
            state = build_configjson_state_schema_b(
                self.fs.dashboards(),
                factory_file=self._factory_state_file,
                image_path=_img_path,
                deleted_ids=self._consume_pending_deleted_ids(),
            )
        else:
            state = build_configjson_state(
                self.fs.dashboards(),
                display_version=self._display_version,
                reset_version=self._reset_version,
                factory_file=self._factory_state_file,
                image_ref_map=_img_ref,
                image_path=_img_path,
                display_product_type=self._display_model_name)
        seq09 = getattr(self, '_session09_next_seq', 0x0200)
        frames = chunk_session_payload(
            self._configjson_session, seq09, state,
            crc_bytes=(0 if self._configjson_session == 0x0a else 4))
        self._session09_next_seq = (seq09 + max(1, (len(state) + 53) // 54)) & 0xFFFF
        with self._pending_lock:
            self._pending_sends.extend(frames)
        self.cat_counts['state_refresh'] = self.cat_counts.get('state_refresh', 0) + len(frames)
        if self.emitter:
            self.emitter.emit_event('state_refresh',
                dashboards=len(self.fs.dashboards()), frames=len(frames),
                schema=('B' if self._configjson_session == 0x0a else 'A'))

    def _emit_schema_b(self, *, upload_in_progress: Optional[str] = None,
                       transitional: bool = False) -> None:
        """Queue a Schema B delta on session 0x0a (KS Pro only). Used for
        upload-lifecycle Phase 1 (mid-upload) and Phase 2 (transitional);
        Phase 3 (re-enumerate) goes through `_fire_state_refresh` instead.

        - Phase 1: `upload_in_progress=<dirname>` → that single dashboard
          appears in `disabledManager.updateDashboards`, everything else
          empty.
        - Phase 2: `transitional=True` → both managers cleared.
        """
        if self._configjson_session != 0x0a:
            return
        _img_ref, _img_path = self.fs.image_manifest()
        if transitional:
            dashboards: List[dict] = []
        elif upload_in_progress:
            # Materialize a minimal record for the in-progress dashboard so
            # the Schema B builder has metadata to emit. Real wheel uses
            # the full per-dashboard record from its FS state; for an
            # in-progress upload the record may not yet exist on disk, so
            # synthesize a placeholder by name.
            dashboards = [{'dirName': upload_in_progress,
                           'title': upload_in_progress, 'hash': ''}]
        else:
            dashboards = self.fs.dashboards()
        state = build_configjson_state_schema_b(
            dashboards,
            factory_file=self._factory_state_file,
            image_path=_img_path,
            upload_in_progress=upload_in_progress,
        )
        seq = getattr(self, '_session09_next_seq', 0x0200)
        frames = chunk_session_payload(self._configjson_session, seq, state,
                                       crc_bytes=0)
        self._session09_next_seq = (seq + max(1, (len(state) + 53) // 54)) & 0xFFFF
        with self._pending_lock:
            self._pending_sends.extend(frames)
        phase = ('phase1_mid_upload' if upload_in_progress
                 else 'phase2_transitional' if transitional
                 else 'unknown')
        self.cat_counts[f'schema_b_{phase}'] = (
            self.cat_counts.get(f'schema_b_{phase}', 0) + len(frames))
        if self.emitter:
            self.emitter.emit_event('schema_b_delta',
                phase=phase, dirname=upload_in_progress, frames=len(frames))

    def _maybe_fire_schema_b_phase1(self, session: int) -> None:
        """Once chunk-0 of an upload is parseable, derive the target dirname
        and emit a Phase 1 Schema B delta. Idempotent per session — only
        fires once per upload-lifecycle (cleared at completion)."""
        if self._configjson_session != 0x0a:
            return
        if session in self._mid_upload_dirname:
            return
        files = self._extract_bundle_file_table(session)
        if not files:
            return
        # First file in the bundle is the .mzdash; dirname = basename minus
        # extension. Bundle preserves PitHouse's authoring directory only
        # in the per-file path field, but the basename strip already matches
        # the dirName our `set_active_dashboard` logic uses post-write.
        first = files[0][0]
        dirname = first.rsplit('.', 1)[0] if '.' in first else first
        if not dirname:
            return
        self._mid_upload_dirname[session] = dirname
        self._emit_schema_b(upload_in_progress=dirname)

    def _consume_pending_deleted_ids(self) -> List[str]:
        """Drain + return the buffered deleted-dashboard ids (Schema B
        `enabledManager.deletedDashboards` payload)."""
        ids = list(self._pending_deleted_ids)
        self._pending_deleted_ids.clear()
        return ids

    def note_deleted_dashboard(self, dashboard_id: str) -> None:
        """Record a deleted dashboard id for inclusion in the next Schema B
        delta. Called from delete paths (completelyRemove RPC, reset RPC)."""
        if dashboard_id and dashboard_id not in self._pending_deleted_ids:
            self._pending_deleted_ids.append(dashboard_id)

    def _save_versions(self) -> None:
        """Persist displayVersion + resetVersion across sim restarts."""
        try:
            self._versions_path.parent.mkdir(parents=True, exist_ok=True)
            self._versions_path.write_text(json.dumps({
                'displayVersion': self._display_version,
                'resetVersion': self._reset_version,
            }))
        except Exception:
            pass

    def _emit_session09_keepalive(self) -> None:
        """Emit periodic session keepalives. Real wheel sends
        `7e 0a c3 71 7c 00 09 01 2b 00 00 00 00 00` on session 0x09
        (~2s interval, observed in latestcaps post-state-push). For
        configjson_session=0x0a (KS Pro), real wheel ALSO emits a 3-byte
        `fc 00 0a` cumulative-ack heartbeat (~4s interval, observed in
        `mozahubstartup.pcapng` t=3.205/7.209/11.211); PitHouse appears
        to require these before treating 0x0a as a live session and
        consuming any state push."""
        body = b'\x7c\x00\x09\x01\x2b\x00\x00\x00\x00\x00'
        frame = build_frame(0xC3, 0x71, body)
        with self._pending_lock:
            self._pending_sends.append(frame)
            if self._configjson_session == 0x0a:
                self._pending_sends.append(
                    build_frame(GRP_WHEEL, DEV_WHEEL_RSP,
                                b'\xfc\x00\x0a'))
        self.cat_counts['session09_keepalive'] = (
            self.cat_counts.get('session09_keepalive', 0) + 1)

    def _extract_bundle_file_table(self, session: int):
        """Parse the multi-file bundle table from chunk0 of the upload buffer.

        Returns a list of (filename, start_offset, end_offset) tuples for
        the decoded bundle, or None if no chunk0 with a parseable table is
        present. Filenames are basenames (no directory) extracted from the
        UTF-16BE PitHouse path. end_offset == 0 for the LAST file (size
        runs to end-of-decompressed-stream).
        """
        import re as _re
        buf = bytes(self._upload_tracker._bufs.get(session, b''))
        if not buf:
            return None
        # Locate chunk0 (counter==0, contains zlib magic in body)
        items = []
        pos = 0
        while pos < len(buf) - 6:
            t = buf[pos]
            if t in (0x01, 0x02, 0x03, 0x11) and buf[pos+3:pos+6] == b'\x00\x00\x00':
                sz = int.from_bytes(buf[pos+1:pos+3], 'little')
                if 50 < sz < 80000 and pos + 6 + sz <= len(buf):
                    items.append((pos, t, sz))
                    pos += 6 + sz
                    continue
            pos += 1
        chunk0 = None
        for o, t, sz in items:
            if t != 3 or not (_UPLOAD_TYPE03_MIN_SIZE < sz < _UPLOAD_TYPE03_MAX_SIZE):
                continue
            body = buf[o+6:o+6+sz]
            if len(body) < 295:
                continue
            counter = int.from_bytes(body[281:284], 'little')
            if counter != 0:
                continue
            zm = _re.search(rb'\x78[\x9c\xda]', body)
            if zm:
                chunk0 = (body, zm.start())
                break
        if chunk0 is None:
            return None
        body, zoff = chunk0
        table = body[291:zoff]
        if len(table) < 8:
            return None
        try:
            file_count = int.from_bytes(table[0:4], 'big')
            if file_count == 0 or file_count > 32:
                return None
            tpos = 4
            entries = []  # (basename, cum_end_offset_or_0)
            for i in range(file_count):
                if tpos + 4 > len(table):
                    return None
                plen = int.from_bytes(table[tpos:tpos+4], 'big')
                tpos += 4
                if plen <= 0 or plen > 1024 or tpos + plen > len(table):
                    return None
                path = table[tpos:tpos+plen].decode('utf-16-be', errors='replace').rstrip('\x00')
                tpos += plen
                # All entries except the LAST have a 4B trailer:
                # u32 BE cumulative_end_offset (where this file ends in the
                # fully-decompressed bundle stream).
                if i < file_count - 1:
                    if tpos + 4 > len(table):
                        return None
                    cum_end = int.from_bytes(table[tpos:tpos+4], 'big')
                    tpos += 4
                else:
                    cum_end = 0  # last file → run to end of decoded stream
                # Take the basename (last path component, '/' or '\\')
                basename = path.replace('\\', '/').rsplit('/', 1)[-1]
                if not basename:
                    return None
                entries.append((basename, cum_end))
            # Convert cumulative-end into per-file (start, end) ranges.
            files = []
            prev_end = 0
            for basename, cum_end in entries:
                files.append((basename, prev_end, cum_end))
                if cum_end:
                    prev_end = cum_end
            return files
        except Exception as _e:
            _swallow('bundle_table_parse', _e)
            return None

    def _parse_upload_6b(self, buf: bytes) -> Optional[bytes]:
        """Decode a 2026-04 new-firmware upload buffer using the 6-byte
        sub-msg header layout. See `_parse_upload` docstring above the call
        site for the per-chunk layout reference. Returns the decompressed
        mzdash bytes (possibly partial — eof not guaranteed when PitHouse
        retransmitted), or None if no chunk0 (counter=0 + zlib magic) is
        present in the buffer.

        Algorithm:
        1. Walk the buffer with the 6B-header validator to enumerate clean
           sub-msg boundaries (stride matches header.size + 6).
        2. Identify chunk0: a type=0x03 chunk whose body[281:284] LE counter
           is 0 AND whose body contains the `78 9c` zlib magic.
        3. Decompress chunk0 starting at the magic offset.
        4. Walk the remaining type=0x03 chunks in BUFFER ORDER. For each, take
           the deflate continuation slice at the FIXED offset body[291:]
           (after shared envelope[0..280] + counter[281..283] + 7B constant
           [284..290]). Append if zlib accepts; skip if zlib raises. Stop on
           EOF or if we run out of chunks.

        Strict offset-291 (no greedy probing) prevents wrong-attempt chunks
        from "succeeding" at coincidental offsets and producing valid-but-wrong
        deflate output (which used to corrupt mzdash bytes past byte ~45000
        for typical 70 KB dashboards).
        """
        import re as _re
        CONT_DEFLATE_OFF = 291  # fixed by protocol — see docstring above
        # Walk the buffer with stride validation. Skip any byte that doesn't
        # produce a clean (header → next header at off + 6 + size) chain.
        items = []
        pos = 0
        while pos < len(buf) - 6:
            t = buf[pos]
            if t in (0x01, 0x02, 0x03, 0x11) and buf[pos+3:pos+6] == b'\x00\x00\x00':
                size = int.from_bytes(buf[pos+1:pos+3], 'little')
                if 50 < size < 80000 and pos + 6 + size <= len(buf):
                    items.append((pos, t, size))
                    pos += 6 + size
                    continue
            pos += 1
        type03 = [(o, sz) for o, t, sz in items if t == 3 and 1000 < sz < 10000]
        if not type03:
            return None
        # Find chunk0 (counter == 0, zlib magic present in body)
        chunk0 = None
        for o, sz in type03:
            body = buf[o+6:o+6+sz]
            if len(body) < CONT_DEFLATE_OFF:
                continue
            counter = int.from_bytes(body[281:284], 'little')
            if counter != 0:
                continue
            zm = _re.search(rb'\x78[\x9c\xda]', body)
            if zm:
                chunk0 = (o, sz, body, zm.start())
                break
        if chunk0 is None:
            return None
        o0, sz0, body0, zoff0 = chunk0
        d = zlib.decompressobj()
        try:
            # Feed chunk0's deflate slice. If the input contains a complete
            # zlib stream the decompressor sets `eof=True` and any trailing
            # padding bytes after EOF land in `unused_data` (we simply drop
            # them). For partial streams (multi-chunk uploads) eof stays
            # False and the loop below appends continuation chunks.
            out = bytearray(d.decompress(body0[zoff0:]))
        except zlib.error:
            return None
        # Walk remaining chunks in BUFFER ORDER (= wire-frame seq order, as the
        # session-buffer is appended in arrival order). Use the counter at
        # body[281:283] as a DEDUPE key only — PitHouse retransmits the same
        # chunk content with the same counter when it doesn't get an ack, and
        # the counter is NOT a strict ordering field across captures. Accept
        # any chunk whose body[291:] cleanly continues the deflate stream;
        # skip retransmits + chunks that don't fit (different attempts).
        seen_counters = {0}  # chunk0
        for o, sz in type03:
            if d.eof:
                break
            if o <= o0:
                continue
            body = buf[o+6:o+6+sz]
            if len(body) <= CONT_DEFLATE_OFF:
                continue
            counter = int.from_bytes(body[281:284], 'little')
            if counter in seen_counters:
                continue
            d_test = d.copy()
            try:
                extra = d_test.decompress(bytes(body[CONT_DEFLATE_OFF:]))
            except zlib.error:
                # Doesn't continue this stream — different attempt or stale.
                # Skip and keep walking; the right chunk may follow later.
                continue
            seen_counters.add(counter)
            d = d_test
            out += extra
        return bytes(out)

    def _parse_upload(self, session: int) -> Optional[dict]:
        """Called after a session END marker on the file-transfer session
        (dynamic port; 0x04..0x0a observed). Reassembles chunks buffered by
        UploadTracker, extracts the UTF-16LE destination path + decompresses
        the zlib body, and registers the dashboard in `stored_dashboards`.
        Returns the new dashboard entry (or None if parse failed)."""
        name = self._upload_tracker.extract_mzdash_path(session)
        buf = bytes(self._upload_tracker._bufs.get(session, b''))
        import re as _re
        try:
            _dbg = _LOG_DIR / 'parse_upload.log'
            with _dbg.open('a') as _f:
                zlib_offs = [m.start() for m in _re.finditer(rb'\x78[\x9c\xda]', buf)]
                _f.write(f'[{time.strftime("%H:%M:%S")}] session=0x{session:02x} name={name!r} buf_len={len(buf)} zlib_offs={zlib_offs}\n')
            # Dump full buffer for offline analysis. Overwrite on each call
            # so the file always reflects the most-recent upload (previous
            # write-once-per-session behaviour stranded us with stale dumps
            # whenever a fresh upload arrived).
            _dump_path = _LOG_DIR / f'parse_upload_sess{session:02x}_buf.bin'
            if len(buf) > 100:
                _dump_path.write_bytes(buf)
        except Exception:
            pass
        if not buf:
            return None
        decomp: Optional[bytes] = None
        # ── New-firmware (2026-04 PitHouse, session 0x09) path: 6B header ──
        # Sub-msg layout under new firmware:
        #   header(6) = type(1) + size_LE(2) + 3B pad(00 00 00)
        #   body(size) =
        #     [0..280]   shared TLV envelope (LOCAL 0x8C path + REMOTE 0x70 path
        #                + md5 + token), identical across all chunks in one
        #                attempt
        #     [281..283] 3B LE counter (0 for first chunk; varies per
        #                continuation, signalling stream offset / position)
        #     [284..289] 7B constant `03 92 16 00 00 0f fc`
        #     IF counter == 0:    body[290..1267) = dest_path TLV +
        #                         compressed_header; body[1267:] = `78 9c` zlib
        #                         stream begin + deflate
        #     IF counter > 0:     body[290:] = raw deflate continuation
        #
        # PitHouse retransmits cause multiple attempts to interleave in a
        # single buffer. We anchor on the (counter==0, has-zlib-magic) chunk
        # and greedily append continuation chunks at offsets that yield clean
        # decode output. Earlier-firmware (session 0x07, 8B header) layout is
        # tried as a fallback below.
        decomp = self._parse_upload_6b(buf)
        if decomp is not None:
            try:
                with _dbg.open('a') as _f:
                    _f.write(f'    6B-layout decoded {len(decomp)}B\n')
            except Exception:
                pass
        # PitHouse splits the dashboard zlib stream across many type=0x03
        # sub-msgs. Only the FIRST msg of each upload attempt carries the
        # `78 9c` zlib magic; subsequent msgs hold raw deflate continuation
        # at the same fixed offset within the msg (8B msg header + LOCAL
        # path TLV + REMOTE path TLV + 0x10 flag + 16B md5 + 4B reserved +
        # 4B token + 8B compressed_header → zlib data starts at offset 769
        # in the type=0x03 sub-msg for the 2026-04 firmware path lengths
        # observed). Reassemble by anchoring on the most recent type=0x02
        # metadata boundary, then concatenating zlib bytes from each
        # following type=0x03 msg at the offset where the first one's magic
        # lives.
        attempts = []
        for m in _re.finditer(rb'\x02..\x00\x00\x00\x00\x00', buf):
            off = m.start()
            size = int.from_bytes(buf[off+1:off+5], 'little')
            if 100 < size < 400:
                attempts.append(off)
        if not attempts and decomp is None:
            return None
        attempt_iter = list(reversed(attempts)) if decomp is None else []
        for a_off in attempt_iter:
            next_a = len(buf)
            for cand in attempts:
                if cand > a_off:
                    next_a = min(next_a, cand)
            seg_end = next_a
            type03_offs = []
            for m in _re.finditer(rb'\x03..\x00\x00\x00\x00\x00', buf[a_off:seg_end]):
                off = m.start() + a_off
                size = int.from_bytes(buf[off+1:off+5], 'little')
                if _UPLOAD_TYPE03_MIN_SIZE < size < _UPLOAD_TYPE03_MAX_SIZE:
                    type03_offs.append((off, size))
            if not type03_offs:
                continue
            first_off, first_size = type03_offs[0]
            first_end = min(first_off + 8 + first_size, len(buf))
            zm = _re.search(rb'\x78[\x9c\xda]', buf[first_off:first_end])
            if not zm:
                continue
            zoff_in_msg = zm.start()
            zdata = bytearray()
            for off, size in type03_offs:
                end = min(off + 8 + size, len(buf))
                if off + zoff_in_msg < end:
                    zdata += buf[off + zoff_in_msg:end]
            # Use decompressobj so partial streams (PitHouse aborted upload
            # mid-flight) still yield whatever bytes did transmit. Better to
            # save partial mzdash than nothing.
            try:
                d = zlib.decompressobj()
                decomp = d.decompress(bytes(zdata))
                if not decomp:
                    decomp = None
                else:
                    try:
                        with _dbg.open('a') as _f:
                            _f.write(f'    decoded {len(decomp)}B from attempt @{a_off} '
                                     f'(zoff_in_msg={zoff_in_msg}, msgs={len(type03_offs)}, zdata={len(zdata)}, eof={d.eof})\n')
                    except Exception:
                        pass
                    break
            except zlib.error as _ze:
                try:
                    with _dbg.open('a') as _f:
                        _f.write(f'    fixed-offset decode FAILED for attempt @{a_off}: {_ze} '
                                 f'(zoff_in_msg={zoff_in_msg}, msgs={len(type03_offs)}, zdata={len(zdata)})\n')
                except Exception:
                    pass
        if decomp is None:
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
        # New-firmware uploads are MULTI-FILE BUNDLES — one decompressed blob
        # contains the mzdash JSON plus N PNG image resources concatenated.
        # The file table is encoded in chunk0 metadata at body[291..zlib_off):
        #   u32 BE file_count
        #   per-file: u32 BE path_byte_length + UTF-16BE path
        #             + (u32 BE cumulative end-offset in decoded bundle
        #                + 4B marker)  for all but the last file
        # The last file omits the trailer; its bytes run from previous
        # cumulative end to end-of-decompressed-stream.
        files = self._extract_bundle_file_table(session) or []
        # Defer wheel_fs.json save until all bundle files are written —
        # otherwise a 6-file bundle rewrites the 1.5MB JSON 6 times.
        with self.fs.batch_writes():
            if files and dirname:
                written_any = False
                for fname, start, end in files:
                    if start >= len(decomp):
                        continue
                    slice_end = min(end, len(decomp)) if end > 0 else len(decomp)
                    if slice_end <= start:
                        continue
                    fs_path = f"{_WHEEL_FW_DASH_ROOT}/{dirname}/{fname}"
                    self.fs.write_file(fs_path, decomp[start:slice_end])
                    written_any = True
                if not written_any:
                    # Fall back to writing whole blob as the mzdash file.
                    fs_path = f"{_WHEEL_FW_DASH_ROOT}/{dirname}/{dirname}.mzdash"
                    self.fs.write_file(fs_path, decomp)
            else:
                # No file table parsed — legacy single-file path.
                fs_path = f"{_WHEEL_FW_DASH_ROOT}/{dirname}/{dirname}.mzdash"
                self.fs.write_file(fs_path, decomp)
        # Also dump the decoded blob to disk for offline diagnosis (overwrites
        # per session). Survives sim_reload/restart unlike the in-memory FS.
        try:
            _dump = _LOG_DIR / f'parse_upload_sess{session:02x}_decoded.bin'
            _dump.write_bytes(decomp)
        except Exception:
            pass
        # Clear buffer so a repeat upload doesn't double-emit.
        self._upload_tracker._bufs[session] = bytearray()
        # KS Pro Schema B Phase 2 — fire the transitional empty-managers
        # delta between file-commit and the re-enumerate (Phase 3) push
        # that `_fire_state_refresh` will issue from set_active_dashboard.
        # Real wheel emits this brief neutral state (verified against
        # device_sess0x0a_b201). Then drop the per-session in-progress
        # marker so the next upload can fire its own Phase 1.
        if session in self._mid_upload_dirname:
            try:
                self._emit_schema_b(transitional=True)
            except Exception as _e:
                _swallow('schema_b_phase2', _e, sim=self)
            self._mid_upload_dirname.pop(session, None)
        # Auto-promote the newly-uploaded dashboard to active. Verified
        # 2026-04-25 (interactive PitHouse test): PitHouse's "active dash"
        # indicator follows the slot returned by 28:00, where slot index
        # = position in advertised list (factory dashes first, then user
        # uploads). After uploading `bbb` and setting active_dash_index=12
        # (= 11 factory + bbb at position 1), PitHouse UI reflected `bbb`
        # as active. PitHouse's set-side switch trigger is not yet RE'd,
        # so without auto-promote here a freshly-uploaded dash never
        # appears active in the UI.
        try:
            self.set_active_dashboard(dirname, pages=1)
        except (ValueError, AttributeError):
            # set_active_dashboard couldn't find the new entry (race?) or
            # the method isn't wired — fall back to plain state refresh.
            self._fire_state_refresh()
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
                import traceback as _tb
                try:
                    _log_path = _LOG_DIR / 'rpc_debug.log'
                    with _log_path.open('a') as _f:
                        _f.write(f"[rpc_err] method={entry.get('method')} err={e}\n")
                        _tb.print_exc(file=_f)
                except Exception:
                    pass
                if self.emitter:
                    self.emitter.emit_event('rpc_err', method=entry.get('method'), err=str(e))

    def _handle_rpc(self, entry: dict) -> None:
        """Dispatch a parsed JSON RPC entry. Stateful handlers mutate
        stored_dashboards; unknown methods get a generic {id, result: true}
        reply so PitHouse's `id` callback fires. Reply is chunked onto the
        same session using the same 9-byte header + zlib stream wire format."""
        try:
            _log_path = _LOG_DIR / 'rpc_debug.log'
            with _log_path.open('a') as _f:
                _f.write(f"[rpc_handle] method={entry.get('method')} arg={entry.get('arg')} id={entry.get('id')} sess={entry.get('session')}\n")
        except Exception:
            pass
        method = entry['method']
        arg = entry['arg']
        rpc_id = entry['id']
        session = entry['session']
        # Default reply value — real wheel mostly replies with empty string
        # (observed on reset: `{"()":"","id":15}` → empty return tracked via
        # matching empty value). Use "" as the safe default; specific RPCs
        # override below.
        result: object = ""
        # Known methods are dispatched here. Additions belong in P2/P4.
        if method == 'completelyRemove':
            removed = 0
            removed_names: List[str] = []
            # Match any dashboard known to the virtual FS by id / dirName /
            # hash / title. PitHouse's `arg` is the id it assigned at upload
            # time; the sim derives its own id from md5 hash, so bare-id
            # match only works for dashboards the sim originally populated.
            # For dashboards PitHouse uploaded itself (its id is random and
            # unknown to the sim), we fall back to hash/dirName/title and —
            # as a last resort — emit whatever it knew PitHouse to be
            # tracking under that id from prior configJson-reply parsing.
            known_ids = getattr(self, '_pithouse_dashboard_ids', {})
            target_dirname = known_ids.get(arg)
            # Also consult UploadTracker's parsed configJson replies — when
            # PitHouse echoes its library back in `configJson()`, each entry
            # has a (name, id) pair we can fall back to.
            if not target_dirname:
                for u in self._upload_tracker.uploaded_dashboards:
                    if u.get('id') == arg and u.get('name'):
                        target_dirname = u['name']
                        self._pithouse_dashboard_ids[arg] = target_dirname
                        break
            # Last-resort fallback: consult the captured factory configJson
            # state. PitHouse's UI may list factory-catalog dashboards
            # (configJsonList) it sees on the wheel, using factory ids stored
            # in its own cache. If `arg` matches a factory id, delete the
            # matching dirName from FS (if present). Check BOTH known factory
            # state files — FS may have been populated under a different
            # model's catalog than the currently-loaded factory file.
            if not target_dirname:
                for fs_file in (self._factory_state_file,
                                _FACTORY_STATE_FILE_W17_RGB,
                                _FACTORY_STATE_FILE_W08_SM):
                    if not fs_file:
                        continue
                    state = _load_factory_configjson_state(fs_file)
                    for d in state.get('enableManager', {}).get('dashboards', []):
                        if d.get('id') == arg:
                            target_dirname = d.get('dirName')
                            break
                    if target_dirname:
                        break
            # No fallback heuristic when arg can't be resolved. Earlier
            # versions guessed "if there's exactly one non-factory dashboard,
            # assume PitHouse means that one" — that silently deleted user
            # uploads when PitHouse called completelyRemove with a stale id
            # after a sim restart (the per-install UUID cache survives the
            # sim restart that wiped `_pithouse_dashboard_ids`). Silent no-op
            # + telemetry is safer than silent destructive guess.
            if not target_dirname and self.emitter:
                self.emitter.emit_event('rpc_delete_unresolved',
                    arg=arg,
                    fs_count=len(self.fs.dashboards()))
            for d in self.fs.dashboards():
                if (d.get('id') == arg
                        or d.get('dirName') == arg
                        or d.get('hash') == arg
                        or d.get('title') == arg
                        or (target_dirname and d.get('dirName') == target_dirname)):
                    n = self.fs.delete(
                        f"{_WHEEL_FW_DASH_ROOT}/{d['dirName']}")
                    if n:
                        removed += n
                        removed_names.append(d['dirName'])
                        # Surface the removed id in the next Schema B delta
                        # so PitHouse sees `enabledManager.deletedDashboards`
                        # match its own UI state. arg is whatever PitHouse
                        # asked us to remove — that's the id PitHouse cares
                        # about, regardless of what the FS recorded.
                        self.note_deleted_dashboard(arg)
            # `stored_dashboards` is a FS-derived property — mutation
            # happens via `self.fs.delete` above; no cache to clear.
            # Real wheel's completelyRemove reply shape is not yet captured
            # verbatim. Use empty string to match the reset-RPC pattern
            # (`{"()":"","id":N}`) which is the only documented wheel reply
            # shape (usb-capture/session-0x0a-rpc-re.md). Echoing the request
            # arg back also works but empty is minimally invasive.
            result = ""
            # Always re-push the configJson state + 0x04 dir listing after a
            # completelyRemove, even when no FS file matched. PitHouse
            # otherwise treats the wheel's view as stale, won't re-evaluate
            # its Dashboard Manager state, and sometimes refuses to initiate
            # a fresh upload of the same dashboard.
            self._fire_state_refresh()
            # Clamp active-dash slot if the deleted dashboard was the active
            # one (or the slot index is now out of range). Without this,
            # 28:00 keeps reporting a stale slot that PitHouse can't resolve
            # to any visible dashboard.
            advertised = self.list_advertised_dashboards()
            if advertised and self.active_dash_index > len(advertised):
                self.active_dash_index = len(advertised)
                self._fire_state_refresh()
            if not removed and self.emitter:
                self.emitter.emit_event('rpc_delete_unmatched',
                    arg=arg, fs_count=len(self.fs.dashboards()))
        elif method == '' and (arg == '' or arg is None):
            # Empty-method `{"()": "", "id": N}` is PitHouse's documented
            # "reset-to-empty" signal (usb-capture/session-0x0a-rpc-re.md).
            # Sent when the user clicks "Reset all" on the device. Wheel must
            # wipe its FS and report empty configJsonList back. Without this,
            # PitHouse keeps showing the pre-reset dashboard list because no
            # other delete RPC fires for the all-clear case.
            removed_reset: List[str] = []
            for d in list(self.fs.dashboards()):
                n = self.fs.delete(
                    f"{_WHEEL_FW_DASH_ROOT}/{d['dirName']}")
                if n:
                    removed_reset.append(d['dirName'])
            # Don't bump resetVersion — firmware-stamped constant, not a
            # cache-bust counter (verified across multiple same-wheel
            # captures: value never changes). Earlier sim incremented it
            # which likely poisoned PitHouse's compatibility check.
            # FS now empty → only factory dashes remain. Snap active to
            # slot 1 (default factory dash) so 28:00 doesn't keep returning
            # a now-vanished user-upload slot.
            self.active_dash_index = 1
            self._fire_state_refresh()
            if removed_reset and self.emitter:
                self.emitter.emit_event('rpc_reset_all',
                    removed=removed_reset, count=len(removed_reset))
            result = ""
        elif method == 'configJson':
            # Host echoes its dashboard library to the wheel as the standard
            # reply to the wheel's session 0x09 / 0x0a state push. arg shape:
            # {'dashboards': [<entry>, …], 'activeDashboardId': <id>?, …}.
            # Stash the entries in `_pithouse_dashboard_ids` so future
            # `completelyRemove(id)` calls can resolve PitHouse-assigned ids
            # back to dirNames; if the reply carries an active-slot signal
            # use it to update `active_dash_index`. Not authoritative for
            # FS mutations — dedicated paths (`completelyRemove(id)` /
            # empty-method reset) drive deletes. Earlier versions wiped any
            # FS dashboard not in the host's list, which on initial connect
            # deleted real user uploads since the host's advertised library
            # is its own profile, not the wheel's FS.
            if isinstance(arg, dict):
                advertised = self.list_advertised_dashboards()
                advertised_names = [d.get('dirName') for d in advertised]
                for entry in arg.get('dashboards', []) or []:
                    if not isinstance(entry, dict):
                        continue
                    eid = entry.get('id') or entry.get('uuid')
                    name = entry.get('dirName') or entry.get('title')
                    if eid and name:
                        self._pithouse_dashboard_ids[eid] = name
                # `activeDashboardId` shape not yet captured; tolerate either
                # a slot index or a name/id string.
                active_signal = (arg.get('activeDashboardId')
                                 or arg.get('activeDashboard')
                                 or arg.get('active'))
                if isinstance(active_signal, int):
                    self.active_dash_index = max(1, active_signal)
                elif isinstance(active_signal, str):
                    target = self._pithouse_dashboard_ids.get(active_signal,
                                                               active_signal)
                    if target in advertised_names:
                        self.active_dash_index = (
                            advertised_names.index(target) + 1)
            result = ""
        # Real wheel RPC reply shape mirrors the request:
        #   {"<method>()": <return>, "id": <same id>}
        # Documented in usb-capture/session-0x0a-rpc-re.md. Earlier sim replied
        # with {"id": N, "result": ...} which PitHouse silently dropped,
        # leaving its Dashboard Manager stuck on the pre-delete state and
        # blocking the subsequent upload.
        reply_obj = {f'{method}()': result, 'id': rpc_id}
        payload = encode_rpc_message(reply_obj)
        seq = self._rpc_seq.get(session, 0x0100)
        frames = chunk_session_payload(session, seq, payload)
        self._rpc_seq[session] = (seq + max(1, (len(payload) + 53) // 54)) & 0xFFFF
        try:
            _log_path = _LOG_DIR / 'rpc_debug.log'
            with _log_path.open('a') as _f:
                _f.write(f"[rpc_reply_queued] method={method} session=0x{session:02x} seq=0x{seq:04x} payload_bytes={len(payload)} frames={len(frames)} reply_obj={reply_obj}\n")
        except Exception:
            pass
        with self._pending_lock:
            self._pending_sends.extend(frames)
        if self.emitter:
            self.emitter.emit_event('rpc_reply',
                method=method, id=rpc_id, session=f'0x{session:02x}',
                frames=len(frames))

    def _scan_file_transfer_paths(self, session: int) -> Tuple[Optional[str], Optional[str], Optional[bytes], int, int]:
        """Extract local path, synthesized remote path, md5 (16B),
        total_size, and host_bytes_written from the host's session 0x06
        upload buffer.

        Host sub-msg wire format (type=0x02 meta or type=0x03 content):
            [type:1] [size_LE:u32] [00 00 00]
            [0x8A 0x00] [UTF-16LE Windows temp path] [0x00 0x00]
            ([0x8A 0x00 | 0x70 0x00] [UTF-16LE second path] [0x00 0x00])?
            [0x10] [md5:16]
            [bytes_written:u32 BE] [total_size:u32 BE]
            [0xFF 0xFF 0xFF 0xFF]

        Returns (local_path, remote_path, md5, total_size, host_bw). The
        wheel's canonical remote path is synthesized from md5:
        `/_moza_filetransfer_md5_<md5hex>`. host_bw is the host's
        most-recent claim of how many bytes IT has sent — when this equals
        total_size, the host is waiting on the wheel's done-ack.
        """
        buf = bytes(self._upload_tracker._bufs.get(session, b''))
        if not buf:
            return (None, None, None, 0, 0)
        local = None
        # Anchor on the LAST type=0x02 metadata sub-msg start so retries with
        # fresh tmp paths don't get confused with stale path TLVs from earlier
        # in the buffer. Two firmware layouts coexist:
        #   Legacy2025_11 (8B header): `02 40 [type] 00 00 00 00 00`
        #   New2026_04   (7B header):  `02 02 [size_lo] [size_hi] 00 00 00`
        # KS Pro 2026-04 firmware uses the new layout exclusively; if we only
        # match legacy the upload metadata is "invisible" and PitHouse times
        # out before sending content (visible symptom: dashboard upload
        # silently no-ops with no wire traffic on the file-transfer session).
        import re as _re_pf
        last_meta = None
        for m in _re_pf.finditer(rb'\x02..\x00\x00\x00\x00\x00', buf):
            last_meta = m.start()
        for m in _re_pf.finditer(rb'\x02\x02..\x00\x00\x00', buf):
            if last_meta is None or m.start() > last_meta:
                last_meta = m.start()
        scan_start = last_meta if last_meta is not None else 0
        # Find first UTF-16 path TLV — host's Windows local temp path.
        # Marker varies by firmware: 0x8a (older), 0x8c (2026-04+).
        off = -1
        for marker in (b'\x8a\x00', b'\x8c\x00'):
            off = buf.find(marker, scan_start)
            if off >= 0:
                break
        if off >= 0:
            start = off + 2
            end = start
            while end + 1 < len(buf):
                if buf[end] == 0 and buf[end + 1] == 0:
                    break
                end += 2
            try:
                local = buf[start:end].decode('utf-16-le', errors='replace').rstrip('\x00')
            except Exception:
                local = None
        # Locate metadata trailer. Layout relative to the 0x10 flag byte:
        #   flag_off+0 ........ 0x10 (flag)
        #   flag_off+1 .. +16 . md5 (16 bytes)
        #   flag_off+17 .. +20  bytes_written (BE u32)
        #   flag_off+21 .. +24  total_size (BE u32)
        #   flag_off+25 .. +28  0xff 0xff 0xff 0xff (sentinel)
        # Preceded by a `0x00 0x00` UTF-16 terminator from the last path TLV.
        md5 = None
        total_size = 0
        host_bw = 0
        scan = scan_start
        # Walk all 0x10-flag matches and prefer the LATEST valid one — host
        # retransmits the metadata sub-msg with advancing bytes_written; the
        # most recent one is its current claim of bytes-sent-so-far.
        latest = None
        while True:
            flag_off = buf.find(b'\x10', scan)
            if flag_off < 0:
                break
            scan = flag_off + 1
            if flag_off < 2 or flag_off + 29 > len(buf):
                continue
            if buf[flag_off - 2:flag_off] != b'\x00\x00':
                continue
            if buf[flag_off + 25:flag_off + 29] != b'\xff\xff\xff\xff':
                continue
            latest = flag_off
        if latest is not None:
            md5 = buf[latest + 1:latest + 17]
            host_bw = int.from_bytes(buf[latest + 17:latest + 21], 'big')
            total_size = int.from_bytes(buf[latest + 21:latest + 25], 'big')
        remote = None
        if md5 is not None:
            remote = f'/_moza_filetransfer_md5_{md5.hex()}'
        return (local, remote, md5, total_size, host_bw)

    def _queue_file_transfer_echo(self, session: int) -> None:
        """Build and queue a type=0x01 or type=0x11 response on the given
        file-transfer session. Session number is dynamic — real PitHouse
        opens one of 0x04..0x08 depending on firmware build and upload-type;
        we detect a file-transfer session by the presence of a type=0x02
        metadata sub-msg in the reassembled session buffer (md5 + ff*4
        sentinel pattern), not by hardcoded port number.

        PitHouse's upload protocol:
          host type=0x02 metadata  →  wheel type=0x01 ready-ack
          host type=0x03 content   →  wheel type=0x11 complete-ack

        Sim picks the response based on what host has actually sent so far.
        Scans the reassembled session buffer for sub-msg type bytes: if a
        type=0x03 chunk has arrived, emit type=0x11 (bytes_written=total);
        otherwise emit type=0x01 (bytes_written=0). Emitting type=0x11 before
        host sends type=0x03 causes PitHouse to error with "wheel claims
        complete but I haven't sent content" → upload stalls.
        """
        if session < 0x04 or session > 0x0f:
            # Sessions 0x01..0x03 are mgmt/telemetry/RPC. 0x0b+ observed in
            # the wild for back-to-back uploads (PitHouse picks a fresh
            # dynamic port for each upload — saw 0x0b on second upload after
            # 0x07 closed). Range widened 2026-04-25.
            return
        local, remote, md5, total_size, host_bw = self._scan_file_transfer_paths(session)
        if md5 is None or remote is None:
            # Host hasn't finished its metadata yet — nothing to ack.
            return
        # Detect PitHouse's "host done" terminator pattern: a small type=0x03
        # sub-msg of size==292 whose body[287:291] is the 0xff*4 sentinel. This
        # is the explicit "I've finished sending" signal — distinct from the
        # metadata sub-msg's declarative `bytes_written == total_size` which is
        # set from the very first send and means nothing about progress.
        # Verified spec via captures + RE notes (docs/moza-protocol.md §
        # "Per-chunk metadata trailer").
        import re as _re_term
        host_done = False
        buf_for_term = bytes(self._upload_tracker._bufs.get(session, b''))
        for _m in _re_term.finditer(rb'\x03\x24\x01\x00\x00\x00', buf_for_term):
            _toff = _m.start()
            if _toff + 6 + 292 > len(buf_for_term):
                continue
            _tbody = buf_for_term[_toff+6:_toff+6+292]
            if len(_tbody) >= 291 and _tbody[287:291] == b'\xff\xff\xff\xff':
                host_done = True
                break
        # Walk type=0x03 sub-msgs. For each unique chunk (dedup by counter at
        # body[281:284]), sum the compressed slice past the 291-byte header
        # AND accumulate the raw compressed bytes for trial-decompress. The
        # wheel emits its final type=0x11 ack only after the decoded stream
        # length reaches the metadata's total_size — older byte-counter logic
        # never fired when total_size was the *uncompressed* size and bytes_
        # received counted compressed bytes (seen in real captures from
        # latestcaps + ksp). Trial-decompress on accumulated content closes
        # the gap.
        buf = bytes(self._upload_tracker._bufs.get(session, b''))
        rounds = 0
        bytes_received = 0
        compressed_content = bytearray()
        seen_ctrs: set = set()
        _pos = 0
        while _pos < len(buf) - 6:
            _t = buf[_pos]
            if _t == 0x03 and buf[_pos+3:_pos+6] == b'\x00\x00\x00':
                _sz = int.from_bytes(buf[_pos+1:_pos+3], 'little')
                if 50 < _sz < 80000 and _pos + 6 + _sz <= len(buf):
                    _body = buf[_pos+6:_pos+6+_sz]
                    if len(_body) > 291:
                        _ctr = int.from_bytes(_body[281:284], 'little')
                        if _ctr not in seen_ctrs:
                            seen_ctrs.add(_ctr)
                            rounds += 1
                            slice_ = _body[291:]
                            bytes_received += len(slice_)
                            compressed_content.extend(slice_)
                    _pos += 6 + _sz
                    continue
            # Fallback to legacy 8B-header walk for older firmware shape.
            if buf[_pos:_pos+1] == b'\x03' and buf[_pos+5:_pos+8] == b'\x00\x00\x00':
                _sz_legacy = int.from_bytes(buf[_pos+1:_pos+5], 'little')
                if 50 < _sz_legacy < 80000 and _pos + 8 + _sz_legacy <= len(buf):
                    rounds += 1
                    _pos += 8 + _sz_legacy
                    continue
            _pos += 1

        # Trial-decompress accumulated content. If a clean zlib stream parses
        # AND the decoded length matches metadata's total_size, treat upload
        # as complete. This is the wheel's own completion gate per real
        # captures (latestcaps + ksp): decode+verify, then emit type=0x11.
        decoded_complete = False
        decoded_size = 0
        if compressed_content:
            # Try decompress from each plausible content-start offset (chunk-0
            # sometimes carries an extra TLV envelope before the zlib magic).
            import zlib as _zlib
            for trial_offset in (0, 411, 976):
                if trial_offset >= len(compressed_content):
                    continue
                try:
                    decoded = _zlib.decompress(bytes(compressed_content[trial_offset:]))
                    decoded_size = len(decoded)
                    # Accept decompression as success if total_size unknown OR
                    # decoded length is within 64 bytes of declared total
                    # (small TLV/padding tolerance).
                    if not total_size or decoded_size + 64 >= total_size:
                        decoded_complete = True
                    break
                except _zlib.error:
                    continue

        # Diagnostic — write per-echo state to track upload progress.
        try:
            _diag = _LOG_DIR / f'ft_echo_sess{session:02x}.log'
            with _diag.open('a') as _f:
                _f.write(
                    f'[{time.strftime("%H:%M:%S")}] sess=0x{session:02x} '
                    f'rounds={rounds} bytes_received={bytes_received} '
                    f'total_size={total_size} host_bw={host_bw} '
                    f'host_done={host_done} '
                    f'decoded_size={decoded_size} '
                    f'decoded_complete={decoded_complete}\n'
                )
        except Exception:
            pass
        if total_size and bytes_received > total_size:
            bytes_received = total_size

        # Ack pattern: PitHouse expects a type=0x01 progress ack per chunk
        # to keep the stream flowing (verified empirically — suppressing
        # progress acks stalls upload at chunk 1). Older latestcaps
        # captures show only 2 acks per upload because those uploads only
        # had 1 type=0x03 chunk; multi-chunk uploads need progress acks.
        #
        #   1. First call (rounds_acked == -1): READY-ACK type=0x01,
        #      bytes_written=0. Sent right after type=0x02 metadata.
        #   2. Subsequent calls before completion: PROGRESS-ACK type=0x01,
        #      bytes_written=bytes_received. Skipped if no new round arrived.
        #   3. Completion (decoded_complete OR bytes_received >= total_size):
        #      DONE-ACK type=0x11, bytes_written=total_size.
        rounds_acked = getattr(self, '_ft_rounds_acked', {}).get(session, -1)
        if rounds_acked < 0:
            submsg_index = 1
            bytes_written = 0
        elif (decoded_complete or host_done
              or (total_size and bytes_received >= total_size)):
            submsg_index = 2
            bytes_written = total_size
        else:
            if rounds <= rounds_acked:
                return
            submsg_index = 1
            bytes_written = bytes_received
        local_path = local or ''
        body = build_file_transfer_response(
            remote_path=remote,
            local_path=local_path,
            md5=md5,
            total_size=total_size,
            bytes_written=bytes_written,
            submsg_index=submsg_index,
        )
        # File-transfer reply path: user's PitHouse (2026-04) appears to use
        # the same 4-byte CRC32 framing on file-transfer chunks as on
        # handshake chunks. Earlier switch to 3-byte CRC here left PitHouse
        # stalled after type=0x02 metadata → type=0x01 ack (no type=0x03
        # content forthcoming). Reverted to 4B 2026-04-24.
        #
        # Device-side seq is independent from host's seq on the same session.
        # Real wheel starts device-side seq at port+1 (e.g. port=6 → first
        # data chunk at seq 0x07). Default to that convention.
        port = self.device_opened_sessions.get(session, session)
        seq = self._ft_reply_next_seq.get(session, (port + 1) & 0xFFFF)
        chunks = chunk_session_payload(session, seq, body, crc_bytes=4)
        seq = (seq + max(1, (len(body) + 53) // 54)) & 0xFFFF
        self._ft_reply_next_seq[session] = seq
        with self._pending_lock:
            self._pending_sends.extend(chunks)
        self.cat_counts['ft_ack'] = self.cat_counts.get('ft_ack', 0) + len(chunks)
        if not hasattr(self, '_ft_rounds_acked'):
            self._ft_rounds_acked = {}
        # Track which "round count" we've already acked. First ack (no content
        # yet) sets to 0. Each progress ack updates to current rounds. Final
        # done-ack sets to a sentinel so no further acks fire.
        if submsg_index == 1:
            self._ft_rounds_acked[session] = rounds
        else:
            self._ft_rounds_acked[session] = 0xFFFF
        self._ft_submsg_emitted = submsg_index
        if self.emitter:
            self.emitter.emit_event('ft_ack',
                session=f'0x{session:02x}', submsg=submsg_index,
                total=total_size, written=bytes_written, chunks=len(chunks))

    def _queue_dash_reply(self, session: int) -> None:
        """Build the 17 wheel→host session-data frames replaying the recorded
        dashboard upload reply stream on `session`, append to _pending_sends.
        Fires once per session (guarded by _upload_replied).

        Gated by `dash_reply_enabled`. Replay mid-upload causes PitHouse to
        skip its file transfer (it believes the dash is already stored). Only
        fire after a real upload parses — `_parse_upload` flips the
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
        this to also drain Timer-queued sends.

        Each `_try_*` helper either returns a non-None response list (and has
        already called `_record`) or returns None to fall through. Order
        matters — earlier handlers take precedence over later ones.
        """
        if not verify(frame) or len(frame) < 4:
            return []

        self.frames_total += 1
        group, device = frame[2], frame[3]

        # ES routing: when --model es selects wheel_device=0x13, drop any frame
        # addressed to 0x17 silently. Real ES wheels don't enumerate at 0x17 —
        # without this, the replay table (built from VGS/CSP captures) would
        # answer 0x17 identity probes with VGS values and confuse PitHouse.
        if device == 0x17 and self.wheel_device != 0x17:
            self._record('es_drop_17', frame)
            return []

        # Stateful wheel handlers (session/tier def/display/telemetry). Returns
        # None if this isn't a known wheel-protocol command so we fall through.
        if group == GRP_HOST and device == self.wheel_device:
            result = self._handle_wheel(frame)
            if result is not None:
                tag, rsp = result
                self._record(tag, frame)
                return rsp

        payload_all = frame_payload(frame)

        # LED bitmask decode is a side-effect-only step before the wheel-write
        # echo handler picks up the same frame. Keep it here, not in a
        # handler, since it must run regardless of which echo path matches.
        self._maybe_decode_led_mask(group, device, payload_all)

        # Ordered dispatch chain. First non-None return wins. Each helper is
        # responsible for calling `_record` with its own tag.
        for _try in (
            self._try_active_dash_query,
            self._try_hub_cfg,
            self._try_plugin_probe,
            self._try_synth_probe,
            self._try_pithouse_identity,
            self._try_device_identity,
            self._try_param_0e,
            self._try_heartbeat,
            self._try_keepalive_43,
            self._try_wheel_write_echo,
            self._try_base_write,
            self._try_pedal_write_echo,
            self._try_base_param_write,
            self._try_replay,
            self._try_wheel_cfg_echo,
        ):
            rsp = _try(frame, group, device, payload_all)
            if rsp is not None:
                return rsp

        # Nothing handled it — record for the gap report with semantic label.
        self._record_unhandled(frame, group, device)
        return []

    # ── Sub-handlers for _handle_core ───────────────────────────────────────
    # Each returns Optional[List[bytes]]. None = fall through. The split
    # mirrors the pre-refactor branch order verbatim — no semantic changes.

    def _try_active_dash_query(self, frame, group, device, payload_all):
        """Active-dashboard config reads (group 0x40 cmd 0x28). Dynamic so
        PitHouse sees the correct active-dash index after
        `sim_set_active_dashboard` is called.

        See pithouse-re.md: 28:00 = WheelGetCfg_GetMultiFunctionSwitch
        (active dash mode index), 28:01 = WheelGetCfg_GetMultiFunctionNum
        (active dash page count). 28:02 stays in the static table — it's
        multi-function telemetry mode, not dash-related."""
        if not (group == 0x40 and device == self.wheel_device
                and len(payload_all) >= 2 and payload_all[0] == 0x28):
            return None
        sub = payload_all[1]
        if sub == 0x00:
            # latestcaps pno 63263: `28 00 00` → `28 00 00 01`. Sim previously
            # returned 3 bytes which PitHouse silently dropped.
            dyn_rsp = bytes([0x28, 0x00, 0x00, self.active_dash_index & 0xFF])
        elif sub == 0x01:
            # putOnWheelAndOpenPitHouse.pcapng frame 38895: `28 01 04 05` =
            # 4 pages, active dash slot 5.
            dyn_rsp = bytes([0x28, 0x01,
                             self.active_dash_pages & 0xFF,
                             self.active_dash_index & 0xFF])
        else:
            return None
        self._record('active_dash_query', frame)
        return [build_frame(group | 0x80, swap_nibbles(device), dyn_rsp)]

    def _try_hub_cfg(self, frame, group, device, payload_all):
        """Hub main settings (group 0x1F, dev 0x12) — typed scalar reads.
        Real wheel echoes the requested sub-cmd in the response payload. The
        capture-derived replay table is broken (collapses sub-cmd echoes by
        length-bucket); see docs/wheel-sim-coverage-findings-2026-04-25.md."""
        if not (group == 0x1F and device == 0x12 and payload_all):
            return None
        pl = bytes(payload_all)
        for L in (3, 2, 1):
            if L > len(pl):
                continue
            sub = pl[:L]
            val = _HUB_CFG_VALUES.get(sub)
            if val is not None:
                self._record('hub_cfg', frame)
                return [build_frame(group | 0x80, swap_nibbles(device), sub + val)]
        return None

    def _try_plugin_probe(self, frame, group, device, payload_all):
        """Plugin post-connect detection probes (wheel/base/hub/dash/pedals/
        handbrake + wheel identity reads). Longest cmd-prefix match wins so
        specific commands override generic group echoes."""
        for plen in (4, 3, 2, 1):
            if len(payload_all) >= plen:
                rsp = self._plugin_probe_rsp.get((group, device, bytes(payload_all[:plen])))
                if rsp is not None:
                    self._record('plugin_probe', frame)
                    return [build_frame(group | 0x80, swap_nibbles(device), rsp)]
        return None

    def _try_synth_probe(self, frame, group, device, payload_all):
        """Plugin ProbeMozaDevice() base/hub probes — echo a framed ack so the
        plugin's `first byte == 0x7E` check passes."""
        synth = _PROBE_SYNTH.get((group, device))
        if synth is None:
            return None
        rsp_group, rsp_dev = synth
        self._record('probe', frame)
        return [build_frame(rsp_group, rsp_dev, bytes(frame_payload(frame)))]

    def _try_pithouse_identity(self, frame, group, device, payload_all):
        """PitHouse identity probes — wheel_device is 0x17 for new-protocol
        wheels, 0x13 for ES. Response device is the nibble-swap."""
        if device != self.wheel_device:
            return None
        key = (group, bytes(frame_payload(frame)))
        id_rsp = self._pithouse_id_rsp.get(key)
        if id_rsp is None:
            return None
        self._record('identity', frame)
        return [build_frame(group | 0x80, self.wheel_device_rsp, id_rsp)]

    def _try_device_identity(self, frame, group, device, payload_all):
        """Per-device identity (hub 0x12, base 0x13, pedal 0x19). Same identity
        cascade as wheel but keyed by (device, group, payload) so each address
        answers with its own values. Replaces per-device replay entries for
        groups 02/04/05/06/07/08/09/0F/10/11."""
        dev_key = (device, group, bytes(frame_payload(frame)))
        dev_id_rsp = self._device_id_rsp.get(dev_key)
        if dev_id_rsp is None:
            return None
        self._record('dev_identity', frame)
        return [build_frame(group | 0x80, swap_nibbles(device), dev_id_rsp)]

    def _try_param_0e(self, frame, group, device, payload_all):
        """Group 0x0E = parameter table read. Format:
            request:  00 [param:u16 BE]
            response: 00 [param:u16 BE] [value:u32 BE]

        PitHouse polls hundreds of param slots per connect. Replay tables
        under `sim/replay/param_0e_dev{XX}.json` carry captured pairs for
        hub (0x12), base (0x13), wheel (0x17). Unknown params fall back to a
        zero value so PitHouse always sees SOMETHING."""
        if group != 0x0E:
            return None
        table = self._param_0e_tables.get(device, {})
        req_hex = bytes(payload_all).hex()
        resp_hex = table.get(req_hex)
        if resp_hex is not None:
            self._record('param_0e', frame)
            return [build_frame(0x8E, swap_nibbles(device), bytes.fromhex(resp_hex))]
        if len(payload_all) == 3:
            fallback = bytes(payload_all) + b'\x00\x00\x00\x00'
            self._record('param_0e_zero', frame)
            return [build_frame(0x8E, swap_nibbles(device), fallback)]
        self._record('fw_debug', frame)
        return []  # group matched but unhandled — terminate dispatch

    def _try_heartbeat(self, frame, group, device, payload_all):
        """Heartbeat (group 0x00, empty payload) — ACK only for simulated
        devices. ACKing phantom devices (0x14, 0x15, 0x18-0x1E) causes
        PitHouse to endlessly probe their identity."""
        if not (group == 0x00 and len(payload_all) == 0):
            return None
        if device in self._simulated_devices:
            self._record('heartbeat', frame)
            return [build_frame(0x80, swap_nibbles(device), b'')]
        return []  # silent drop — device not present, but group matched

    def _try_keepalive_43(self, frame, group, device, payload_all):
        """Bare 0x43 connection-keepalive ping (n=1, payload=0x00). Only ACK
        for simulated devices; stray keepalives to dash/wheel-21 silently
        drop."""
        if not (group == GRP_HOST and len(payload_all) == 1 and payload_all[0] == 0x00):
            return None
        if device in self._simulated_devices:
            self._record('keepalive_43', frame)
            return [build_frame(GRP_WHEEL, swap_nibbles(device), b'\x80')]
        return []

    def _maybe_decode_led_mask(self, group, device, payload_all) -> None:
        """Decode RPM/button LED bitmasks (0x3F/0x17/1A:00 and 1A:01).
        Side-effect-only — payload byte 2..3 is a little-endian 16-bit mask,
        bit N = LED N on. Runs unconditionally before the echo handler so
        `rpm_led_mask` / `button_led_mask` track every write."""
        if not (group == 0x3F and device == 0x17
                and len(payload_all) >= 4
                and payload_all[0] == 0x1A
                and payload_all[1] in (0x00, 0x01)):
            return
        mask = int.from_bytes(bytes(payload_all[2:4]), 'little')
        if payload_all[1] == 0x00:
            self.rpm_led_mask = mask
        else:
            self.button_led_mask = mask

    def _try_wheel_write_echo(self, frame, group, device, payload_all):
        """Wheel write ACKs (group 0x3F/0x3E to dev 0x17) — real wheel echoes
        the full request payload for these cmd-prefixes. Keeps us from
        relying on payload-keyed replay for commands whose data varies per
        call (LED index, channel CC, brightness…)."""
        for plen in (2, 1):
            if len(payload_all) >= plen:
                if (group, device, bytes(payload_all[:plen])) in _WHEEL_ECHO_PREFIXES:
                    self._record('wheel_write', frame)
                    return [build_frame(group | 0x80, swap_nibbles(device), bytes(payload_all))]
        return None

    def _try_base_write(self, frame, group, device, payload_all):
        """Base settings writes (group 0x29 to dev 0x13) — PitHouse pushes
        hub config with variable payloads. Real hub echoes them."""
        if not (group == 0x29 and device == 0x13):
            return None
        self._record('base_write', frame)
        return [build_frame(group | 0x80, swap_nibbles(device), bytes(payload_all))]

    def _try_pedal_write_echo(self, frame, group, device, payload_all):
        """Pedal settings writes (group 0x24 to dev 0x19). PitHouse streams
        these at ~50 Hz while the user adjusts pedal calibration sliders;
        real pedals echo them verbatim. Without a handler each write hits
        the unhandled counter and PitHouse retransmits each cmd >1000 times
        per session waiting for an ack — verified 2026-04-26 KS Pro run
        (31k unhandled frames, 25 unique sub-cmds × ~1252 retries each).
        Active only when the model has a `pedal_identity` block (KS Pro
        is the only currently-supported wheel with on-bus pedals)."""
        if not (group == 0x24 and device == 0x19):
            return None
        if 0x19 not in self._simulated_devices:
            return None
        self._record('pedal_write', frame)
        return [build_frame(group | 0x80, swap_nibbles(device), bytes(payload_all))]

    def _try_base_param_write(self, frame, group, device, payload_all):
        """Base param-table writes (group 0x2A dev 0x13). Wire pattern
        verified across 5 captures (pithouse-switch + 3 moza-startup +
        putOnWheelAndOpenPitHouse). See `_BASE_PARAM_ACK_VAL` for the
        sub-cmd → ack-byte transformations."""
        if not (group == 0x2A and device == 0x13):
            return None
        pl = bytes(payload_all)
        ack = pl
        if len(pl) >= 3 and pl[0] == 0x43 and pl[1] == 0x01:
            # Active-dash commit (firmware log: "Table 11, Param 21 Written").
            self.active_dash_index = pl[2] & 0xFF
            ack = pl  # echo
        elif len(pl) >= 3:
            fixed = _BASE_PARAM_ACK_VAL.get(pl[:2])
            if fixed is not None:
                ack = pl[:2] + bytes([fixed])
        self._record('base_param_write', frame)
        return [build_frame(group | 0x80, swap_nibbles(device), ack)]

    def _try_replay(self, frame, group, device, payload_all):
        """Replay layer — stateless query/response pairs recorded from
        captures. SKIP for session-multiplexed frames (group 0x43 dev 0x17,
        payload starts with 0x7c) — file transfer / state push / RPC live
        there and need dynamic responses."""
        if self._replay is None:
            return None
        payload_first = payload_all[0] if payload_all else None
        if group == 0x43 and device == 0x17 and payload_first == 0x7C:
            return None
        recorded = self._replay.lookup(frame)
        if not recorded:
            return None
        self.replay_hits += 1
        self._record('replay', frame)
        return [recorded]

    def _try_wheel_cfg_echo(self, frame, group, device, payload_all):
        """Wheel config reads (group 0x40 to wheel device) — fallback echo
        for queries not in the replay table. Keeps PitHouse from stalling on
        LED config reads whose exact payloads vary per session."""
        if not (group == 0x40 and device == self.wheel_device):
            return None
        self._record('wheel_cfg_echo', frame)
        return [build_frame(0xC0, self.wheel_device_rsp, bytes(payload_all))]

    def _record_unhandled(self, frame, group, device) -> None:
        """Bookkeeping for frames no handler claimed. Updates counters and
        emits a one-shot `unhandled` event the first time each (group, dev,
        cmd) tuple is seen."""
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

    def _handle_wheel(self, frame: bytes) -> Optional[Tuple[str, List[bytes]]]:
        """Wheel-protocol handler. Returns (tag, responses) if the command is
        recognised; returns None if the command isn't a wheel command we know,
        so the caller can fall through to replay."""
        payload = frame_payload(frame)
        if len(payload) < 2:
            return None

        cmd1, cmd2 = payload[0], payload[1]
        responses: List[bytes] = []

        # ── FC:00 session cumulative ACK (both directions) ─────────────────
        # Host periodically emits `fc 00 [sess] [seq_lo] [seq_hi]` meaning "I
        # have acked up to seq X on session Y". Wheel does the same in reverse.
        # Nothing to reply with — consume silently. Without this branch, the
        # replay table answers with a stale capture-time ack value that
        # doesn't match sim's current session state, which causes PitHouse to
        # re-transmit session data (observed as tier-def retry loop).
        if cmd1 == 0xFC and cmd2 == 0x00 and len(payload) >= 3:
            session = payload[2]
            peer_ack = 0
            if len(payload) >= 5:
                peer_ack = payload[3] | (payload[4] << 8)
            self._peer_session_acks = getattr(self, '_peer_session_acks', {})
            self._peer_session_acks[session] = peer_ack
            return ('session_peer_ack', [])

        # ── 7C:00 session management ────────────────────────────────────────
        if cmd1 == 0x7C and cmd2 == 0x00 and len(payload) >= 4:
            session = payload[2]
            msg_type = payload[3]
            tag = 'session'

            if msg_type == SESSION_TYPE_OPEN:
                tag = 'session_open'
                # Re-handshake detection: host-initiated open on session 0x01
                # (mgmt) while we already have sessions open means SimHub/plugin
                # restarted or switched profiles. Reset per-connection state so
                # the new handshake starts clean. Keep wheel-level identity +
                # persistent filesystem + cumulative counters.
                if session == 0x01 and self._device_init_started:
                    self._reset_connection_state()
                # Per-session buffer reset on re-open. PitHouse may re-open a
                # session it previously used (e.g. 0x09 was used for configJson
                # RPC, then re-opened for a dashboard upload). Without clearing
                # the prior session bytes, the upload-metadata parser scans
                # contaminated buffers and bails — manifests as upload stuck
                # at 0% (verified 2026-04-25).
                if session in self._bufs:
                    self._bufs[session] = ChunkBuffer()
                if session in self._upload_tracker._bufs:
                    self._upload_tracker._bufs[session] = bytearray()
                if hasattr(self, '_dir_listing_next_seq'):
                    self._dir_listing_next_seq.pop(session, None)
                self._ft_reply_next_seq.pop(session, None) if hasattr(self, '_ft_reply_next_seq') else None
                self._upload_replied.discard(session)
                self._upload_next_seq.pop(session, None)
                self._upload_last_ff_ts.pop(session, None)
                self._ft_rounds_acked.pop(session, None) if hasattr(self, '_ft_rounds_acked') else None
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
                    # Retain on self so the GC can't finalize the Timer and cancel
                    # it before its 150ms fire — locals would otherwise drop out
                    # of scope as soon as this branch returns.
                    self._device_init_timer = threading.Timer(_DEVICE_INIT_DELAY_SEC, self._fire_device_init)
                    self._device_init_timer.daemon = True
                    self._device_init_timer.start()

            elif msg_type == SESSION_TYPE_DATA and len(payload) >= 6:
                tag = 'session_data'
                # PitHouse resumes old sessions after sim restart (no session_open).
                # Trigger device-init burst here too — plugin remembers
                # sessions across sim-restart and only sends data, never a
                # fresh OPEN. Without this branch, _fire_device_init never
                # fires on resume and the dashboard manager UI never sees
                # the wheel.
                if self.sessions_opened == 0 and not self._reconnect_detected:
                    self._reconnect_detected = True
                    if self.emitter:
                        self.emitter.emit_event('reconnect')
                    if not self._device_init_started:
                        self._device_init_started = True
                        self._device_init_timer = threading.Timer(
                            _DEVICE_INIT_DELAY_SEC, self._fire_device_init)
                        self._device_init_timer.daemon = True
                        self._device_init_timer.start()
                seq = payload[4] | (payload[5] << 8)
                chunk = bytes(payload[6:])
                if session not in self._bufs:
                    self._bufs[session] = ChunkBuffer()
                self._bufs[session].add(seq, chunk)
                responses.append(resp_session_ack(session, seq))
                # Per-session chunk counter (S4 research: which session carries traffic).
                self.session_data_counts[session] = self.session_data_counts.get(session, 0) + 1
                # Also mirror into cat_counts so stale MCP servers still show
                # the per-session breakdown via sim_counters without restart.
                _session_tag = f'session_0x{session:02x}'
                self.cat_counts[_session_tag] = self.cat_counts.get(_session_tag, 0) + 1
                # Feed every chunk through the upload tracker. Scans buffered
                # chunks for zlib streams, decompresses, and extracts dashboard
                # metadata from mzdash/configJson content.
                blob = self._upload_tracker.feed(session, chunk)
                if blob is not None and self.emitter:
                    self.emitter.emit_event('upload_decoded',
                        session=f'0x{session:02x}', size=blob['size'],
                        kind=('json' if blob.get('json') else
                              'utf16' if blob.get('utf16') else 'binary'))
                # KS Pro Schema B Phase 1 — fire the mid-upload delta as
                # soon as chunk-0's bundle file table becomes parseable.
                # Real wheel emits this while file bytes are still inbound
                # (verified against device_sess0x0a_b200 in the
                # mozahubstartup capture).
                if 0x04 <= session <= 0x0f:
                    try:
                        self._maybe_fire_schema_b_phase1(session)
                    except Exception as _e:
                        _swallow('schema_b_phase1', _e, sim=self)
                # Drain any newly-parsed RPC calls → dispatch + queue replies.
                self._drain_rpc_log()

                # Directory-listing probe (host sub-msg type 0x08). Originally
                # observed on session 0x04; session number is dynamic (PitHouse
                # picks one of 0x04..0x0a depending on firmware build / UI
                # flow). Detect by content — `08 2c 00 00 00 00 00 00 14 00
                # [UTF-16 "/home/root"] ff*8 [8B echo_id] ff ff ff ff ...`.
                # Without a type=0x0a reply, PitHouse stalls here and silently
                # falls back to its local cache, skipping any subsequent
                # dashboard upload.
                if (0x04 <= session <= 0x0f and len(chunk) >= 45
                        and chunk[0] == 0x08
                        and chunk[8:10] == b'\x14\x00'):
                    # Probe layout:
                    #   [0..7]   08 + size_LE + 3B pad
                    #   [8..9]   14 00 (path length field; field value = 20
                    #            but only 19B of path follow — firmware quirk)
                    #   [10..28] 19B UTF-16LE "/home/root" (no null terminator)
                    #   [29..36] ff*8
                    #   [37..44] 8B echo_id (request identifier)
                    echo_id = bytes(chunk[37:45])
                    # Replay captured 221B reply byte-exact (with fresh
                    # echo_id). Minimal 45B variant was being rejected by
                    # PitHouse; full captured bytes at least pass the format
                    # check even if they carry "11 factory dashboards" info
                    # (which PitHouse's cache reconciles separately).
                    reply_body = build_dir_listing_reply()
                    # Per-session reply seq counter — real wheel starts at
                    # (port + 1). Sim previously used a single shared
                    # counter (`_session04_next_seq`) which produced seq=1
                    # on session 0x05 instead of seq=6, making PitHouse
                    # reject the reply and stall the upload at 0%.
                    seqs = getattr(self, '_dir_listing_next_seq', None)
                    if seqs is None:
                        seqs = {}
                        self._dir_listing_next_seq = seqs
                    rseq = seqs.get(session, session + 1)
                    reply_chunks = chunk_session_payload(session, rseq, reply_body)
                    seqs[session] = rseq + max(
                        1, (len(reply_body) + 53) // 54)
                    with self._pending_lock:
                        self._pending_sends.extend(reply_chunks)
                    self.cat_counts['dir_listing'] = (
                        self.cat_counts.get('dir_listing', 0) + len(reply_chunks))
                    if self.emitter:
                        self.emitter.emit_event('dir_listing_reply',
                            session=f'0x{session:02x}', chunks=len(reply_chunks),
                            echo_id=echo_id.hex())

                # File-transfer per-sub-msg ack — any session opened via the
                # host's 7c:23 trigger could carry an upload. Real PitHouse
                # opens several ports (0x04, 0x06, 0x07, 0x08 observed during
                # handshake) and picks one dynamically for each upload. Only
                # arm the ack timer if the session buffer already shows a
                # type=0x02 sub-msg header (`02 XX XX XX XX 00 00 00`) —
                # avoids false-triggering on plain session keepalives /
                # RPC traffic during handshake.
                _session_buf = self._upload_tracker._bufs.get(session, b'')
                # Match both header shapes:
                #   Legacy2025_11: `[02 40 type 00 00 00 00 00]` (8B, byte 0=0x02
                #                  marker, zeros at bytes 5-7)
                #   New2026_04:    `[type 02 size_lo size_hi 00 00 00]` (7B, byte 0=type
                #                  which equals 0x02 for path-push metadata,
                #                  byte 1=0x02 marker, zeros at bytes 4-6)
                # KS Pro firmware (verified real-wheel pcap) only uses the new
                # shape; matching only legacy left sim silent on KS Pro
                # uploads, so PitHouse never saw a type=0x01 ready-ack and
                # short-circuited the upload UI before sending content.
                _has_type_02 = any(
                    len(_session_buf) >= off + 8
                    and _session_buf[off] == 0x02
                    and (_session_buf[off + 5:off + 8] == b'\x00\x00\x00'
                         or (_session_buf[off + 1] == 0x02
                             and _session_buf[off + 4:off + 7] == b'\x00\x00\x00'))
                    for off in range(0, max(0, len(_session_buf) - 7))
                )
                if 0x04 <= session <= 0x0f and chunk and _has_type_02:
                    self._ft_received_bytes += len(chunk)
                    if self._ft_echo_timer is not None:
                        try:
                            self._ft_echo_timer.cancel()
                        except Exception:
                            pass
                    self._ft_echo_timer = threading.Timer(
                        _FILE_TRANSFER_ECHO_IDLE_MS / 1000.0,
                        self._queue_file_transfer_echo, args=(session,))
                    self._ft_echo_timer.daemon = True
                    self._ft_echo_timer.start()

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
                # If this was a file-transfer session (dynamic port), parse
                # the uploaded mzdash ONLY when sim already acked completion
                # via type=0x11 (`_ft_rounds_acked[session] == 0xFFFF`).
                # PitHouse sends SESSION_END both on success AND on its own
                # internal abort/timeout — parsing the buffer in the abort
                # case writes a partial/corrupt file to FS that PitHouse's
                # UI already considers "uploaded" because the session
                # closed cleanly. Without the gate, users see "uploads
                # finish at random points" — symptom verified 2026-04-26
                # in sim/logs/parse_upload.log + ft_echo_sess07.log.
                if (session in self.device_opened_sessions
                        and 0x04 <= session <= 0x0f):
                    if self._ft_rounds_acked.get(session) == 0xFFFF:
                        # _parse_upload triggers state refresh internally via
                        # set_active_dashboard auto-promote — no extra
                        # _fire_state_refresh here.
                        self._parse_upload(session)
                    else:
                        # Incomplete upload — clear the buffer instead of
                        # decoding partial content. Surface via emitter so
                        # the user can see when PitHouse aborted mid-stream.
                        partial_bytes = len(
                            self._upload_tracker._bufs.get(session, b''))
                        self._upload_tracker._bufs[session] = bytearray()
                        self._mid_upload_dirname.pop(session, None)
                        if self.emitter:
                            self.emitter.emit_event('upload_aborted',
                                session=f'0x{session:02x}',
                                bytes_buffered=partial_bytes,
                                rounds_acked=self._ft_rounds_acked.get(
                                    session, -1))
                    # Reset per-session ack state so the next OPEN on this
                    # port starts clean.
                    self._ft_rounds_acked.pop(session, None)
                    self._ft_reply_next_seq.pop(session, None)
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
        # 7C:23 = dashboard-activate / session-open request
        # 7C:27 = page-cycle config
        # 7C:1E = display settings push (brightness/timeout/orientation)
        #
        # Specific 7C:23 variant `7c 23 46 80 XX 00 YY 00 fe 01` (10B) is a
        # session-open REQUEST from host: bytes [6:8] = requested port (LE
        # u16). Wheel opens the requested session in response, allowing the
        # host to then stream upload content on that session. Without this
        # reply, PitHouse's UI blocks: dir-listing OK, upload button clicked,
        # but no wire traffic on session 0x06.
        #
        # Captured variants (pithouse-switch-list-delete-upload-reupload.pcapng
        # + automobilista2-wheel-connect-dash-change.pcapng):
        #   `7c 23 46 80 08 00 06 00 fe 01` → open session 0x06 (x3)
        if (cmd1 == 0x7C and cmd2 == 0x23 and len(payload) >= 8
                and payload[2] == 0x46 and payload[3] == 0x80
                and payload[7] == 0x00):
            req_port = payload[6]
            responses.append(resp_device_session_open(req_port, req_port))
            # Track the dynamically-opened session so the session_end handler
            # can gate `_parse_upload` on it and the mzdash content actually
            # gets extracted after the upload completes.
            self.device_opened_sessions[req_port] = req_port
            # Reset per-session buffers — same logic as `7c 00 [sess] 81`
            # OPEN handler. PitHouse's 7c:23 trigger re-uses sessions that
            # may still hold prior data (esp. 0x09 which carries configJson
            # state pushes); without clearing, upload-metadata parser scans
            # contaminated buffers and bails out → upload stuck at 0%.
            if req_port in self._bufs:
                self._bufs[req_port] = ChunkBuffer()
            if req_port in self._upload_tracker._bufs:
                self._upload_tracker._bufs[req_port] = bytearray()
            if hasattr(self, '_dir_listing_next_seq'):
                self._dir_listing_next_seq.pop(req_port, None)
            if hasattr(self, '_ft_reply_next_seq'):
                self._ft_reply_next_seq.pop(req_port, None)
            self._upload_replied.discard(req_port)
            self._upload_next_seq.pop(req_port, None)
            self._upload_last_ff_ts.pop(req_port, None)
            if hasattr(self, '_ft_rounds_acked'):
                self._ft_rounds_acked.pop(req_port, None)
            if self.emitter:
                self.emitter.emit_event('session_open_triggered',
                    port=req_port, cmd='7c:23')
            return 'display_cfg', responses
        if cmd1 == 0x7C and cmd2 in (0x1E, 0x23, 0x27):
            return 'display_cfg', responses

        # ── 7D:23 telemetry frame ─────────────────────────────────────────
        # Min payload is the 8-byte header (cmd[2] + prefix[4] + flag + const).
        # Earlier `>= 10` rejected tiers with single-byte data (e.g. uint8_t
        # FuelRemainLaps in a 500ms tier) — payload is 9 bytes total — and
        # mis-tagged them as "unhandled", hiding tier 1 frames in diagnostics.
        if cmd1 == 0x7D and cmd2 == 0x23 and len(payload) >= 8:
            self.frames_telem += 1
            flag = payload[6]
            telem_data = bytes(payload[8:])
            channels = self.tiers.get(flag, self.channels)
            if channels:
                try:
                    self.values.update(decode_telemetry(telem_data, channels))
                except Exception as _e:
                    _swallow('telem_decode', _e, sim=self)
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
        if sim.tiers:
            tier_summary = ', '.join(
                f'flag=0x{f:02X}:{len(ch)}ch' for f, ch in sorted(sim.tiers.items()))
            lines.append(f'Tier def:       {len(sim.channels)} channels  [{tier_summary}]')
        else:
            lines.append(f'Tier def:       {len(sim.channels)} channels received')
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
    # Box width: idx(3) + sp(1) + name(22) + sp(1) + comp(15) + sp(1) + bits(4) + sp(1) + val(14) = 62
    # Total inside: 62 + 4 padding = 66
    inner_w = 66
    top    = '┌─ Live Values ' + '─' * (inner_w - 14) + '┐'
    bottom = '└' + '─' * inner_w + '┘'
    lines.append(top)

    def _fmt_val(v):
        if isinstance(v, float):
            if v != v:
                return 'NaN'
            return f'{v:.4g}'
        return str(v)

    # Build sections: one per tier if tier def received, else fallback to merged list.
    tier_items = []
    if sim.tiers:
        for flag in sorted(sim.tiers.keys()):
            tier_items.append((flag, sim.tiers[flag]))
    elif sim.channels:
        tier_items.append((None, sim.channels))

    # Cap channel rows so counters / recent-frames stay on screen.
    term_rows_cap = shutil.get_terminal_size(fallback=(80, 24)).lines
    # Reserve ~10 rows for status/counters/LEDs/recent-header below.
    max_channel_rows = max(6, term_rows_cap - len(lines) - 12)

    if not tier_items:
        content = '  (waiting for tier definition…)'
        lines.append(f'│{content:<{inner_w}}│')
    else:
        rows_written = 0
        truncated = 0
        for flag, channels in tier_items:
            if flag is not None and len(tier_items) > 1:
                if rows_written >= max_channel_rows:
                    truncated += len(channels)
                    continue
                header = f'  Tier flag=0x{flag:02X}  ({len(channels)} channels)'
                lines.append(f'│{header:<{inner_w}}│')
                rows_written += 1
            for ch in channels:
                if rows_written >= max_channel_rows:
                    truncated += 1
                    continue
                idx = ch.get('index', 0)
                name = ch.get('name', '?')
                comp = ch.get('compression', '?')
                bits = ch.get('bit_width', 0)
                val = sim.values.get(name)
                vstr = _fmt_val(val) if val is not None else '—'
                if len(name) > 22:
                    name = name[:21] + '…'
                if len(comp) > 15:
                    comp = comp[:14] + '…'
                row = f'  {idx:>2} {name:<22} {comp:<15} {bits:>3}b {vstr:<14}'
                lines.append(f'│{row:<{inner_w}}│')
                rows_written += 1
        if truncated:
            more = f'  … +{truncated} more (resize terminal to see all)'
            lines.append(f'│{more:<{inner_w}}│')
    lines.append(bottom)
    rpm = ''.join('[*]' if sim.rpm_led_mask & (1 << i) else '[ ]'
                  for i in range(sim.rpm_led_count))
    btn = ''.join('(*)' if sim.button_led_mask & (1 << i) else '( )'
                  for i in range(sim.button_led_count))
    lines.append(f'RPM LEDs:  {rpm}')
    lines.append(f'Buttons:   {btn}')
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
        term_rows = shutil.get_terminal_size(fallback=(80, 24)).lines
        # Leave 1 row for prompt + the 'Recent:' header itself.
        budget = max(0, term_rows - len(lines) - 2)
        if budget:
            lines.append('Recent:')
            for tag, hx in list(sim.recent_frames)[:budget]:
                lines.append(f'  [{tag:<13}] {hx[:72]}')

    # Clear to end of screen after last line so a shrinking section (e.g.
    # fewer recent frames, narrower terminal) doesn't leave stale rows.
    print('\n'.join(lines) + '\033[J', end='', flush=True)

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

    Each chunk gets a 4-byte CRC32-LE trailer. Matches what the sim has
    always emitted on catalog / channel-URL enumeration and what user
    PitHouse (2026-04 build) accepts on both handshake and file-transfer
    reply paths.
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
        #
        # Real CSP wheel (2026-04 firmware) follows the preamble with the
        # full channel catalog on session 1 too — gated by
        # `session1_emits_catalog` flag on the profile.
        s1_frames = _chunk_catalog_message(0x01, b'\xff', start_seq=4)
        s1_frames += _chunk_catalog_message(
            0x01, bytes.fromhex('030400000001000000'), start_seq=5)
        s1_seq_next = 6
        if channel_urls and model.get('session1_emits_catalog'):
            s1_msg = _build_session2_message(channel_urls)
            s1_frames += _chunk_catalog_message(0x01, s1_msg, start_seq=s1_seq_next)
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
             output_mode: str = 'interactive',
             rpm_led_count: int = _DEFAULT_RPM_LED_COUNT,
             button_led_count: int = _DEFAULT_BUTTON_LED_COUNT,
             factory_state_file: Optional[str] = None,
             proactive_session09: bool = True,
             configjson_session: int = 0x09):
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

    log_path = _LOG_DIR / 'wheel_sim.log'
    log_fh = _open_session_log(log_path, port)
    print(f'[Logging to {log_path} (rotated last 5)]', file=sys.stderr)

    sim = WheelSimulator(db, replay, device_catalog,
                         rpm_led_count=rpm_led_count,
                         button_led_count=button_led_count,
                         factory_state_file=factory_state_file,
                         proactive_session09=proactive_session09,
                         configjson_session=configjson_session)
    emitter = None
    if output_mode != 'interactive':
        emitter = ConsoleEmitter(json_mode=(output_mode == 'json'))
        sim.emitter = emitter
        global _SWALLOW_EMITTER
        _SWALLOW_EMITTER = emitter
    alive = threading.Event()
    alive.set()
    write_lock = threading.Lock()

    def _write(frame: bytes, tag: str):
        # Build the full escaped wire buffer first, then emit in ONE ser.write()
        # call. Per-byte ser.write (previous implementation) caused tty0tty /
        # Wine to deliver bytes piecewise; when sim fired a burst of 7 session
        # 0x09 state chunks (~490B total) in 40 ms, SimHub's plugin side saw
        # only the last chunk — Wine's SerialPort polling fell behind per-byte
        # syscall cadence and dropped earlier frames. Single-syscall write
        # fixes it (2026-04-22).
        body = bytearray(frame[:2])
        for b in frame[2:]:
            body.append(b)
            if b == MSG_START:
                body.append(MSG_START)
        ser.write(bytes(body))
        log_fh.write(f'{_ts()} TX [{tag:<13}] {frame.hex(" ")}\n')

    def read_loop():
        while alive.is_set():
            try:
                frame = read_one_frame(ser)
                if frame is None:
                    # Empty read — peer may have closed the pty (SimHub stop)
                    # or sent a partial frame. Don't exit the loop; just back
                    # off briefly and keep reading. When SimHub restarts, the
                    # plugin reopens the tty and fresh frames flow in.
                    time.sleep(_READ_LOOP_BACKOFF_EMPTY_SEC)
                    continue
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
                # Real I/O failure (peer fully gone). Back off and retry — pty
                # may still exist after a SimHub restart, so don't kill the thread.
                time.sleep(_READ_LOOP_BACKOFF_ERROR_SEC)
                continue

    # Resolve which 7c:23 frame set to use; default preserves legacy behavior.
    frames_7c23 = c7_23_frames if c7_23_frames is not None else _7C_23_FRAMES

    def proactive_sender():
        time.sleep(_PROACTIVE_STARTUP_DELAY_SEC)

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
                time.sleep(_PROACTIVE_7C23_BURST_SPACING_SEC)
        else:
            with write_lock:
                log_fh.write(f'{_ts()} -- [proactive   ] 7c:23 burst skipped (model does not emit)\n')

        # Phase 2: wait for session opens, then send channel catalog.
        catalog_sessions = sorted(sim._device_catalog.keys())
        if not catalog_sessions:
            return

        while alive.is_set() and sim.sessions_opened < 2 and not sim._reconnect_detected:
            time.sleep(_PROACTIVE_SESSION_OPEN_POLL_SEC)
        if not alive.is_set():
            return

        time.sleep(_PROACTIVE_CATALOG_SETTLE_SEC)
        # Catalog may already have been queued by _fire_device_init (which now
        # emits it on every handshake — startup AND reconnect). Skip the
        # one-shot proactive emit in that case to avoid duplicate sends.
        if sim.catalog_sent:
            with write_lock:
                log_fh.write(f'{_ts()} -- [proactive   ] catalog already sent by _fire_device_init, skipping\n')
        else:
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
                    time.sleep(_PROACTIVE_CATALOG_FRAME_SPACING_SEC)

            sim.catalog_sent = True
            if sim.emitter:
                sim.emitter.emit_event('catalog_sent', frames=sim.proactive_sent)
        # Trigger device-init burst (device-side session opens + 0x09 state
        # push for proactive-09 models) if it didn't fire from the
        # session_open path. Reconnect-detected state means PitHouse resumed
        # an existing session — the old session_open branch never ran, so
        # nothing else fires the burst on its own. Without this, VGS/CSP
        # never re-push their configJson state on sim-restart and PitHouse's
        # Dashboard Manager UI shows nothing.
        with write_lock:
            log_fh.write(f'{_ts()} -- [proactive   ] device_init_started={sim._device_init_started} sessions_opened={sim.sessions_opened}\n')
        if not sim._device_init_started:
            sim._device_init_started = True
            try:
                sim._fire_device_init()
                with write_lock:
                    log_fh.write(f'{_ts()} -- [proactive   ] _fire_device_init done\n')
            except Exception as e:
                with write_lock:
                    log_fh.write(f'{_ts()} -- [proactive   ] _fire_device_init FAILED: {type(e).__name__}: {e}\n')
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
            time.sleep(_PROACTIVE_7C23_PERIOD_SEC)

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
                time.sleep(_RENDER_TICK_SEC)
        else:
            while alive.is_set():
                if emitter:
                    emitter.emit_stats(sim)
                time.sleep(_STATS_TICK_SEC)
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
    global _PLUGIN_PROBE_RSP, _PITHOUSE_ID_RSP, _DEVICE_ID_RSP, _DISPLAY_MODEL_NAME, _WHEEL_DEVICE
    model = WHEEL_MODELS[args.model]
    _PLUGIN_PROBE_RSP, _PITHOUSE_ID_RSP, _DEVICE_ID_RSP = _build_identity_tables(model)
    _DISPLAY_MODEL_NAME = model.get('display', {}).get('name', '')
    _WHEEL_DEVICE = model.get('wheel_device', DEV_WHEEL)
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
    # Per-model JSON replay tables take precedence: load them first so their
    # entries win on key collisions with any pcap-sourced fallback.
    model_tables = model.get('replay_tables') or []
    if model_tables:
        replay = ResponseReplay()
        for rel in model_tables:
            abs_path = Path(__file__).parent.parent / rel
            if not abs_path.exists():
                print(f'[WARN] replay_table {rel} not found', file=sys.stderr)
                continue
            added = replay.load_json(str(abs_path))
            print(f'[Replay JSON: +{added} entries from {rel}]', file=sys.stderr)
    if replay_paths:
        if replay is None:
            replay = ResponseReplay()
        for p in replay_paths:
            added = replay.load_pcapng(p)
            print(f'[Replay: loaded {added} new entries from {p}]', file=sys.stderr)
    if replay is not None:
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
        frames_7c23 = {'CSP': _7C_23_FRAMES_CSP, 'VGS': _7C_23_FRAMES_VGS,
                       'KSPRO': _7C_23_FRAMES_KSPRO}.get(
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
        frames_7c23 = {'CSP': _7C_23_FRAMES_CSP, 'VGS': _7C_23_FRAMES_VGS,
                       'KSPRO': _7C_23_FRAMES_KSPRO}.get(
            frames_name, _7C_23_FRAMES_CSP)
        cmd_live(args.port, db, replay, device_catalog,
                 emits_7c23=bool(model.get('emits_7c23', True)),
                 c7_23_frames=frames_7c23,
                 c7_23_reps=int(model.get('_7c23_reps', 13)),
                 catalog_capture_open_seqs=catalog_capture_open_seqs,
                 output_mode=output_mode,
                 rpm_led_count=int(model.get('rpm_led_count', _DEFAULT_RPM_LED_COUNT)),
                 button_led_count=int(model.get('button_led_count', _DEFAULT_BUTTON_LED_COUNT)),
                 factory_state_file=model.get('factory_state_file'),
                 proactive_session09=bool(model.get('proactive_session09', True)),
                 configjson_session=int(model.get('configjson_session', 0x09)))
    else:
        parser.print_help()
        sys.exit(1)

if __name__ == '__main__':
    main()
