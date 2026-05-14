"""MCP server for the MOZA AB9 active-shifter simulator.

Exposes `Ab9Simulator` state and live-mutation knobs as MCP tools so Claude
Code can drive plugin / PitHouse smoke tests against a synthetic AB9 without
needing real hardware. Runs as a stdio MCP server; pair with the matching
`.mcp.json` entry that launches `python3 sim/ab9_sim.py --mcp <port>`.

Surface:

  Lifecycle:
    ab9_start(port)        — open serial port, spawn read loop
    ab9_stop()             — close port (signals cross-process owner if held)
    ab9_info()             — running flag + configured port

  Inspection:
    ab9_status()           — uptime, frame counts, current mode + sliders
    ab9_settings()         — every stored 0x1E setting (mode, sliders, analog)
    ab9_recent(count, ...) — recent frames from the rolling ring buffer
    ab9_counters()         — per-tag handler counters
    ab9_unhandled()        — (group, device, payload-prefix) drops

  Mutation:
    ab9_set_mode(mode)     — write shifter-mode stored value (D3)
    ab9_set_slider(name, value)
                           — write slider stored value (mech_resistance,
                             spring, natural_damping, natural_friction,
                             max_torque_limit)
    ab9_set_analog(x, y)   — set shifter X/Y analog values returned on the
                             next D7/D8 0x1E poll (16-bit signed semantics)
    ab9_engage_gear(gear)  — convenience: shift analog into "engaged"
                             quadrant matching one of 1..7 / R / N

Cross-process port lock at `/tmp/ab9_sim_<slug>.lock` (parallel to wheel
sim's `wheel_sim_<slug>.lock`). One AB9 sim per port across all processes.
"""

from __future__ import annotations

import json
import os
import platform
import re
import signal
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Dict, List, Optional

try:
    import fcntl as _fcntl
    _HAS_FCNTL = True
except ImportError:
    _HAS_FCNTL = False
    import msvcrt as _msvcrt  # type: ignore

from mcp.server.fastmcp import FastMCP

_server = FastMCP("ab9-sim")
_IS_WINDOWS = platform.system() == 'Windows'

# ── Lifecycle state ─────────────────────────────────────────────────────────

_sim = None           # Ab9Simulator instance
_session = None       # _Ab9Session instance
_config: Dict[str, object] = {}
_last_disconnect: float = 0.0
_COOLDOWN_SEC = 3.0   # shorter than wheel sim — AB9 has no proactive senders

_lock_fh = None
_lock_path: Optional[Path] = None


# ── Session wrapping read loop ──────────────────────────────────────────────

class _Ab9Session:
    """Wraps serial port + read thread for one AB9 sim run.

    Mirrors `mcp_server._SimSession.start` but without the wheel's proactive
    sender / catalog burst / dash-upload reply loop — AB9 is a pure responder.
    """

    def __init__(self, ser, sim, log_fh, alive, write_lock,
                 wire_trace_fh=None):
        self.ser = ser
        self.sim = sim
        self.log_fh = log_fh
        self.wire_trace_fh = wire_trace_fh
        self.alive = alive
        self.write_lock = write_lock
        self._threads: list = []

    def start(self) -> None:
        from ab9_sim import MSG_START  # type: ignore
        from wheel_sim import read_one_frame, _ts  # type: ignore

        ser = self.ser
        sim = self.sim
        log_fh = self.log_fh
        wire_trace_fh = self.wire_trace_fh
        alive = self.alive
        write_lock = self.write_lock

        def _emit_wire_trace(direction: str, frame: bytes) -> None:
            if wire_trace_fh is None:
                return
            wire_trace_fh.write(json.dumps({
                't': time.time(),
                'dir': direction,
                'hex': frame.hex(),
                'len': len(frame),
            }) + '\n')

        def _write(frame: bytes, tag: str) -> None:
            body = bytearray(frame[:2])
            for b in frame[2:]:
                body.append(b)
                if b == MSG_START:
                    body.append(MSG_START)
            ser.write(bytes(body))
            log_fh.write(f'{_ts()} TX [{tag:<14}] {frame.hex(" ")}\n')
            sim.frames_tx += 1
            sim.recent_frames.append(('tx', tag, frame.hex()))
            _emit_wire_trace('b2h', frame)

        def read_loop() -> None:
            import serial as _serial  # type: ignore
            while alive.is_set():
                try:
                    frame = read_one_frame(ser)
                    if frame is None:
                        # peer pty closed; back off, keep retrying — same
                        # rationale as wheel_sim.cmd_live (SimHub restart
                        # reopens the tty).
                        time.sleep(0.05)
                        continue
                    sim.last_handler_tag = ''
                    responses = sim.handle(frame)
                    tag = sim.last_handler_tag or (
                        'silent_drop' if not responses else 'unknown')
                    sim.frames_rx += 1
                    sim.recent_frames.append(('rx', tag, frame.hex()))
                    with write_lock:
                        log_fh.write(
                            f'{_ts()} RX [{tag:<14}] {frame.hex(" ")}\n')
                        _emit_wire_trace('h2b', frame)
                        for rsp in responses:
                            _write(rsp, tag)
                except (OSError, _serial.SerialException):
                    time.sleep(0.5)
                    continue

        t = threading.Thread(target=read_loop, name='ab9_read_loop',
                             daemon=True)
        t.start()
        self._threads = [t]

    def stop(self) -> None:
        self.alive.clear()
        try:
            self.ser.close()
        except Exception:
            pass
        try:
            self.log_fh.close()
        except Exception:
            pass
        if self.wire_trace_fh is not None:
            try:
                self.wire_trace_fh.close()
            except Exception:
                pass
            self.wire_trace_fh = None
        for t in self._threads:
            t.join(timeout=2.0)


def configure(*, port: str) -> None:
    """Store config for lazy ab9_start. Called from ab9_sim.py --mcp."""
    _config['port'] = port


# ── Cross-process port lock ─────────────────────────────────────────────────

def _port_lock_path(port: str) -> Path:
    slug = re.sub(r'[^A-Za-z0-9]+', '_', port).strip('_') or 'default'
    return Path(tempfile.gettempdir()) / f'ab9_sim_{slug}.lock'


def _try_file_lock(fh) -> bool:
    try:
        if _HAS_FCNTL:
            _fcntl.flock(fh.fileno(), _fcntl.LOCK_EX | _fcntl.LOCK_NB)
        else:
            fh.seek(0)
            _msvcrt.locking(fh.fileno(), _msvcrt.LK_NBLCK, 1)
        return True
    except (OSError, BlockingIOError):
        return False


def _release_file_lock(fh) -> None:
    try:
        if _HAS_FCNTL:
            _fcntl.flock(fh.fileno(), _fcntl.LOCK_UN)
        else:
            fh.seek(0)
            _msvcrt.locking(fh.fileno(), _msvcrt.LK_UNLCK, 1)
    except OSError:
        pass


def _read_lockfile(path: Path) -> dict:
    try:
        return json.loads(path.read_text())
    except Exception:
        return {}


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if _IS_WINDOWS:
        out = subprocess.run(
            ['tasklist', '/FI', f'PID eq {pid}', '/NH'],
            capture_output=True, text=True,
        )
        return str(pid) in out.stdout
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def _kill_pid(pid: int) -> None:
    if _IS_WINDOWS:
        subprocess.run(
            ['taskkill', '/F', '/PID', str(pid)],
            capture_output=True, check=False,
        )
    else:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass


def _acquire_port_lock(port: str) -> Optional[dict]:
    global _lock_fh, _lock_path
    path = _port_lock_path(port)
    fh = open(path, 'a+')
    if not _try_file_lock(fh):
        existing = _read_lockfile(path)
        pid = int(existing.get('pid', 0) or 0)
        if pid and _pid_alive(pid):
            fh.close()
            return {
                'pid': pid,
                'port': existing.get('port', port),
                'started': existing.get('started'),
            }
        if not _try_file_lock(fh):
            fh.close()
            return {'pid': pid, 'port': port, 'stale': True}
    fh.seek(0)
    fh.truncate()
    fh.write(json.dumps({
        'pid': os.getpid(),
        'port': port,
        'started': time.time(),
    }))
    fh.flush()
    _lock_fh = fh
    _lock_path = path
    return None


def _release_port_lock() -> None:
    global _lock_fh, _lock_path
    if _lock_fh is not None:
        _release_file_lock(_lock_fh)
        try:
            _lock_fh.close()
        except Exception:
            pass
        _lock_fh = None
    if _lock_path is not None:
        try:
            _lock_path.unlink()
        except Exception:
            pass
        _lock_path = None


# ── Helpers ─────────────────────────────────────────────────────────────────

def _no_sim() -> dict:
    return {'error': 'Simulator not running. Call ab9_start first.'}


def _load_ab9_sim():
    """Import ab9_sim lazily so the MCP server can boot before pyserial is
    installed (smoke checks on a stripped env). Returns the module."""
    sys.path.insert(0, str(Path(__file__).parent))
    import ab9_sim  # type: ignore
    return ab9_sim


# ── Lifecycle tools ─────────────────────────────────────────────────────────

@_server.tool()
def ab9_start(port: Optional[str] = None,
              wire_trace: Optional[str] = None) -> dict:
    """Start the AB9 simulator on a serial port.

    Args:
      port: Override the configured port (e.g. `/dev/ttyGS1`). When omitted,
        uses the port passed to `configure()` at MCP startup.
      wire_trace: Optional path to a JSONL wire-trace file. Schema matches
        bridge-*.jsonl / wheel_sim wire trace: one {t, dir, hex, len} object
        per frame; `tools/moza_trace.py` consumes it.
    """
    global _sim, _session, _last_disconnect

    if _session is not None:
        return {'error': 'Simulator already running',
                'port': _config.get('port', '')}

    if _last_disconnect > 0:
        remaining = _COOLDOWN_SEC - (time.monotonic() - _last_disconnect)
        if remaining > 0:
            time.sleep(remaining)

    use_port = port or _config.get('port', '')
    if not use_port:
        return {'error': 'No port specified. Pass `port=` or run ab9_sim.py --mcp <port>.'}

    conflict = _acquire_port_lock(use_port)
    if conflict:
        return {
            'error': f'Port {use_port} already in use by another ab9_sim (pid {conflict.get("pid")})',
            'owner': conflict,
            'hint': 'Call ab9_stop from any session to kill the owner.',
        }

    try:
        import serial  # type: ignore
    except ImportError:
        _release_port_lock()
        return {'error': 'pyserial not installed. pip install pyserial.'}

    try:
        ser = serial.Serial(use_port, baudrate=115200, timeout=None)
    except (serial.SerialException, OSError) as e:
        _release_port_lock()
        return {'error': f'Cannot open {use_port}: {e}'}

    ab9 = _load_ab9_sim()
    log_path = Path(__file__).parent / 'logs' / 'ab9_sim.log'
    log_fh = ab9._open_session_log(log_path, use_port)
    print(f'[MCP ab9_start] port={use_port}', file=sys.stderr)

    wire_trace_fh = None
    if wire_trace:
        try:
            wt_path = Path(wire_trace)
            wt_path.parent.mkdir(parents=True, exist_ok=True)
            wire_trace_fh = open(wt_path, 'w', buffering=1)
            print(f'[MCP ab9_start] wire trace JSONL → {wt_path}',
                  file=sys.stderr)
        except OSError as e:
            ser.close()
            log_fh.close()
            _release_port_lock()
            return {'error': f"Cannot open wire trace '{wire_trace}': {e}"}

    sim = ab9.Ab9Simulator()
    _sim = sim
    alive = threading.Event()
    alive.set()
    write_lock = threading.Lock()
    session = _Ab9Session(ser, sim, log_fh, alive, write_lock,
                          wire_trace_fh=wire_trace_fh)
    session.start()
    _session = session

    result = {'status': 'running', 'port': use_port}
    if wire_trace:
        result['wire_trace'] = wire_trace
    return result


@_server.tool()
def ab9_stop() -> dict:
    """Stop the simulator and close the serial port. If the sim is owned by a
    different process, sends SIGTERM/taskkill to the owner PID recorded in
    the port lockfile."""
    global _sim, _session, _last_disconnect

    if _session is not None:
        _session.stop()
        _session = None
        _sim = None
        _last_disconnect = time.monotonic()
        _release_port_lock()
        return {'status': 'stopped'}

    use_port = _config.get('port', '')
    if not use_port:
        return {'error': 'Simulator not running'}
    path = _port_lock_path(use_port)  # type: ignore[arg-type]
    if not path.exists():
        return {'error': 'Simulator not running'}
    info = _read_lockfile(path)
    pid = int(info.get('pid', 0) or 0)
    if not pid or not _pid_alive(pid):
        try:
            path.unlink()
        except Exception:
            pass
        return {'error': 'Simulator not running'}
    if pid == os.getpid():
        _release_port_lock()
        return {'status': 'stopped', 'note': 'cleared stale local lock'}

    _kill_pid(pid)
    deadline = time.monotonic() + 3.0
    while time.monotonic() < deadline and _pid_alive(pid):
        time.sleep(0.1)
    still_alive = _pid_alive(pid)
    if not still_alive:
        try:
            path.unlink()
        except Exception:
            pass
    _last_disconnect = time.monotonic()
    return {
        'status': 'stopped' if not still_alive else 'signal_sent',
        'cross_process': True,
        'killed_pid': pid,
    }


@_server.tool()
def ab9_info() -> dict:
    """Connection info: running flag and configured port."""
    return {'running': _session is not None,
            'port': _config.get('port', '')}


# ── Inspection tools ────────────────────────────────────────────────────────

@_server.tool()
def ab9_status() -> dict:
    """Current AB9 sim state: uptime, frame counts, mode, slider summary."""
    if _sim is None:
        return _no_sim()
    return _sim.snapshot()


@_server.tool()
def ab9_settings() -> dict:
    """All stored 0x1E settings (mode, sliders, analog, status flag) as a
    flat hex-keyed dict.  Useful for raw debugging when you need every
    captured value, not the friendly snapshot."""
    if _sim is None:
        return _no_sim()
    return {
        f'0x{cmd:02X}': val
        for cmd, val in sorted(_sim.settings.items())
    }


@_server.tool()
def ab9_recent(count: int = 20,
               direction: Optional[str] = None,
               tag: Optional[str] = None,
               exclude: Optional[str] = None) -> list:
    """Recent frames from the rolling ring buffer (capacity ~2000).

    Args:
      count: Maximum number of frames to return (most recent last).
      direction: 'rx' for host→AB9, 'tx' for AB9→host, omit for both.
      tag: Comma-separated whitelist of handler tags
        (e.g. `read_d3,write_d6`).
      exclude: Comma-separated blacklist (e.g. `heartbeat,read_d7,read_d8`
        to suppress noisy 1-kHz analog polling).
    """
    if _sim is None:
        return _no_sim()
    frames = list(_sim.recent_frames)
    if direction:
        frames = [f for f in frames if f[0] == direction]
    if tag:
        wanted = {t.strip() for t in tag.split(',')}
        frames = [f for f in frames if f[1] in wanted]
    if exclude:
        skip = {t.strip() for t in exclude.split(',')}
        frames = [f for f in frames if f[1] not in skip]
    if count > 0:
        frames = frames[-count:]
    return [{'dir': d, 'tag': t, 'hex': h} for d, t, h in frames]


@_server.tool()
def ab9_counters() -> dict:
    """Per-tag handler counters (heartbeat, id_*, read_*, write_*, ffb_*,
    drop:*, unknown). Useful for verifying the plugin's probe cascade
    completed."""
    if _sim is None:
        return _no_sim()
    result = dict(_sim.cat_counts)
    result['frames_rx'] = _sim.frames_rx
    result['frames_tx'] = _sim.frames_tx
    return result


@_server.tool()
def ab9_unhandled() -> dict:
    """Frames the dispatcher dropped — keyed by (group, dev, payload-prefix).
    Surfaces unknown probes that need new handlers."""
    if _sim is None:
        return _no_sim()
    items = []
    for (g, d, hex_prefix), count in sorted(
            _sim.unhandled_counts.items(), key=lambda x: -x[1]):
        items.append({
            'group':       f'0x{g:02X}',
            'device':      f'0x{d:02X}',
            'payload_hex': hex_prefix,
            'count':       count,
        })
    return {'unique': len(items), 'items': items}


# ── Mutation tools ──────────────────────────────────────────────────────────

@_server.tool()
def ab9_set_mode(mode) -> dict:
    """Write the stored shifter mode (0x1E:D3) — the value returned on the
    next read by PitHouse or the plugin.

    Accepts either an integer byte (0x00, 0x04, 0x05, 0x06, 0x07, 0x09) or
    a friendly name (`'5+R Layout 1'`, `'6+R Layout 1'`, `'6+R Layout 2'`,
    `'7+R Layout 1'`, `'7+R Layout 2'`, `'Sequential'`).
    """
    if _sim is None:
        return _no_sim()
    ab9 = _load_ab9_sim()
    labels = ab9._MODE_LABELS
    if isinstance(mode, str):
        # Case-insensitive label match
        match = None
        for k, v in labels.items():
            if v.lower() == mode.lower():
                match = k
                break
        if match is None:
            try:
                match = int(mode, 0)  # accept '0x09' / '9' too
            except ValueError:
                return {'error': f"Unknown mode '{mode}'. "
                                 f"Known: {sorted(labels.values())}"}
        mode_byte = match
    else:
        try:
            mode_byte = int(mode)
        except (TypeError, ValueError):
            return {'error': f'mode must be int or label, got {type(mode).__name__}'}

    if not (0 <= mode_byte <= 0xFF):
        return {'error': f'mode byte 0x{mode_byte:02X} out of range'}

    _sim.settings[0xD3] = mode_byte
    return {
        'mode': mode_byte,
        'label': labels.get(mode_byte, 'unknown'),
    }


@_server.tool()
def ab9_set_slider(name: str, value: int) -> dict:
    """Write a slider's stored value (0..100). The next read on group 0x1E
    cmd <name>'s byte will return this value; plugin's response parser will
    pick it up on the next `RequestAllStoredSettings` round.

    Args:
      name: One of `mech_resistance`, `spring`, `natural_damping`,
        `natural_friction`, `max_torque_limit`, `mode`. Pass `mode` only as
        a fallback — prefer `ab9_set_mode` which accepts labels.
      value: 0..100 (mode accepts 0..255).
    """
    if _sim is None:
        return _no_sim()
    ab9 = _load_ab9_sim()
    name_l = name.strip().lower().replace('-', '_')
    cmd = ab9.SLIDER_CMDS.get(name_l)
    if cmd is None:
        return {'error': f"Unknown slider '{name}'. "
                         f'Known: {sorted(ab9.SLIDER_CMDS.keys())}'}
    if cmd != 0xD3 and not (0 <= value <= 100):
        return {'error': f'value {value} out of range — sliders are 0..100'}
    if not (0 <= value <= 0xFF):
        return {'error': f'value {value} out of byte range'}
    _sim.settings[cmd] = int(value)
    return {'slider': name_l, 'cmd': f'0x{cmd:02X}', 'value': int(value)}


@_server.tool()
def ab9_set_analog(x: int, y: int) -> dict:
    """Set the shifter X/Y analog values returned on the next D7/D8 reads
    (group 0x1E). Values are 16-bit unsigned (0..65535); in the capture the
    centre is X≈0x66E7 / Y≈0x8001 and engaged-gear positions hit the rails
    (0x0000 / 0xFFFF / 0xE0xx).

    Use this to simulate stick deflection. For a quick gear engagement, see
    `ab9_engage_gear`.
    """
    if _sim is None:
        return _no_sim()
    if not (0 <= x <= 0xFFFF) or not (0 <= y <= 0xFFFF):
        return {'error': 'x and y must each fit in uint16 (0..65535)'}
    _sim.settings[0xD7] = int(x)
    _sim.settings[0xD8] = int(y)
    return {'shifter_x': int(x), 'shifter_y': int(y)}


# Approximate engagement quadrants pulled from the Launch capture spreadsheet
# (`Moza AB9.xlsx`). Values are uint16 X/Y readings during a held gear; signs
# follow the firmware's internal mapping (not strictly little-endian signed
# axes). These are illustrative — real positions vary by physical lever
# travel — but they're close enough that the plugin's monitor UI shows
# clear engagement quadrants.
_GEAR_ANALOG: Dict[str, tuple] = {
    'N':  (0x66E7, 0x8001),
    '1':  (0xE039, 0x0000),
    '2':  (0xB000, 0x0000),
    '3':  (0x66E7, 0x0000),
    '4':  (0xE039, 0xFFFF),
    '5':  (0xB000, 0xFFFF),
    '6':  (0x66E7, 0xFFFF),
    '7':  (0x4000, 0xFFFF),
    'R':  (0xB400, 0x0008),
}


@_server.tool()
def ab9_engage_gear(gear: str) -> dict:
    """Snap the analog X/Y to an approximate engagement quadrant for one of
    `N` (neutral), `1`..`7`, or `R`. Useful for plugin / UI smoke tests
    without actually wiggling a stick. Values are illustrative — real
    positions vary by physical lever travel.

    Note: this does NOT update the HID gear-state bitfield (the sim doesn't
    expose HID). Plugin AB9 detection / mode state is unaffected; only the
    next D7/D8 CDC reads will reflect the new analog position.
    """
    if _sim is None:
        return _no_sim()
    key = str(gear).strip().upper()
    if key not in _GEAR_ANALOG:
        return {'error': f"Unknown gear '{gear}'. "
                         f'Known: {sorted(_GEAR_ANALOG.keys())}'}
    x, y = _GEAR_ANALOG[key]
    _sim.settings[0xD7] = x
    _sim.settings[0xD8] = y
    return {'gear': key, 'shifter_x': x, 'shifter_y': y}


# ── Entry point used by ab9_sim.py --mcp ────────────────────────────────────

def run_stdio() -> None:
    """Run the MCP server on stdio (FastMCP default transport)."""
    _server.run()
