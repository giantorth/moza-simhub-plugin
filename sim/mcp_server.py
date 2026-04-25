"""
MCP server for the MOZA Wheel Simulator.

Exposes simulator state as MCP tools so Claude Code can query telemetry,
session status, and protocol diagnostics. Also provides sim_start/sim_stop
tools to control the serial connection lifecycle.

Runs as a stdio MCP server. Configuration (port, model params, replay table,
device catalog) is passed in via configure() before starting.
"""

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
    import msvcrt as _msvcrt

from mcp.server.fastmcp import FastMCP

_server = FastMCP("wheel-sim")
_IS_WINDOWS = platform.system() == 'Windows'

# ── Lifecycle state ──────────────────────────────────────────────────────────

_sim = None           # WheelSimulator instance (created on sim_start)
_session = None       # _SimSession instance (serial + threads)
_config = {}          # Stored by configure()
_last_disconnect = 0.0  # monotonic timestamp of last sim_stop
_COOLDOWN_SEC = 7.0

# Cross-process port lock. Only one wheel_sim may bind a given serial port
# across all MCP server processes (wheel-sim-linux, wheel-sim-windows, etc.).
# Lock file is keyed by port slug; OS advisory lock on the fh guarantees
# auto-release on process death.
_lock_fh = None
_lock_path: Optional[Path] = None


class _SimSession:
    """Wraps serial port + read/proactive threads for one simulator run."""

    def __init__(self, ser, sim, log_fh, alive, write_lock,
                 emits_7c23, frames_7c23, c7_23_reps):
        self.ser = ser
        self.sim = sim
        self.log_fh = log_fh
        self.alive = alive
        self.write_lock = write_lock
        self._threads: list = []
        self._emits_7c23 = emits_7c23
        self._frames_7c23 = frames_7c23
        self._c7_23_reps = c7_23_reps

    def start(self):
        from wheel_sim import (read_one_frame, annotate, frame_payload,
                               MSG_START, _ts, _7C_23_FRAMES_CSP)
        ser = self.ser
        sim = self.sim
        log_fh = self.log_fh
        alive = self.alive
        write_lock = self.write_lock

        def _write(frame: bytes, tag: str):
            ser.write(frame[:2])
            for b in frame[2:]:
                ser.write(bytes([b]))
                if b == MSG_START:
                    ser.write(b'\x7e')
            log_fh.write(f'{_ts()} TX [{tag:<13}] {frame.hex(" ")}\n')

        def read_loop():
            import serial as _serial
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
                except (OSError, _serial.SerialException):
                    break

        emits_7c23 = self._emits_7c23
        frames_7c23 = self._frames_7c23 if self._frames_7c23 is not None else _7C_23_FRAMES_CSP
        c7_23_reps = self._c7_23_reps

        def proactive_sender():
            time.sleep(0.3)
            if emits_7c23 and frames_7c23:
                reps = max(1, c7_23_reps)
                total = reps * len(frames_7c23)
                with write_lock:
                    log_fh.write(f'{_ts()} -- [proactive   ] 7c:23 burst start ({len(frames_7c23)} variants × {reps} = {total} frames)\n')
                for i in range(total):
                    if not alive.is_set():
                        return
                    frame = frames_7c23[i % len(frames_7c23)]
                    with write_lock:
                        _write(frame, 'proactive')
                    sim.proactive_sent += 1
                    time.sleep(0.0002)

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

            for sess_id in catalog_sessions:
                if sess_id not in (0x01, 0x02):
                    continue
                for frame in sim._device_catalog[sess_id]:
                    if not alive.is_set():
                        return
                    with write_lock:
                        _write(frame, 'catalog')
                    sim.proactive_sent += 1
                    time.sleep(0.001)

            sim.catalog_sent = True
            if sim.emitter:
                sim.emitter.emit_event('catalog_sent', frames=sim.proactive_sent)
            with write_lock:
                log_fh.write(f'{_ts()} -- [proactive   ] catalog complete, {sim.proactive_sent} frames sent\n')

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

        from wheel_sim import dash_upload_reply_loop
        t_read = threading.Thread(target=read_loop, daemon=True)
        t_proactive = threading.Thread(target=proactive_sender, daemon=True)
        t_dash_reply = threading.Thread(
            target=dash_upload_reply_loop,
            args=(sim, alive, write_lock, log_fh, _write), daemon=True)
        t_read.start()
        t_proactive.start()
        t_dash_reply.start()
        self._threads = [t_read, t_proactive, t_dash_reply]

    def stop(self):
        self.alive.clear()
        try:
            self.ser.close()
        except Exception:
            pass
        try:
            self.log_fh.close()
        except Exception:
            pass
        for t in self._threads:
            t.join(timeout=2.0)


def configure(*, port: str, db: dict, replay, device_catalog: dict,
              emits_7c23: bool, c7_23_frames, c7_23_reps: int,
              catalog_capture_open_seqs: dict, model: dict) -> None:
    """Store config for lazy sim_start. Called from wheel_sim.py main()."""
    _config.update({
        'port': port,
        'db': db,
        'replay': replay,
        'device_catalog': device_catalog,
        'emits_7c23': emits_7c23,
        'c7_23_frames': c7_23_frames,
        'c7_23_reps': c7_23_reps,
        'catalog_capture_open_seqs': catalog_capture_open_seqs,
        'model': model,
    })


def _port_lock_path(port: str) -> Path:
    slug = re.sub(r'[^A-Za-z0-9]+', '_', port).strip('_') or 'default'
    return Path(tempfile.gettempdir()) / f'wheel_sim_{slug}.lock'


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
    """Acquire cross-process lock for `port`. Returns None on success;
    returns owner info dict when port is already held by a live process."""
    global _lock_fh, _lock_path
    path = _port_lock_path(port)
    fh = open(path, 'a+')
    if not _try_file_lock(fh):
        existing = _read_lockfile(path)
        pid = int(existing.get('pid', 0) or 0)
        if pid and _pid_alive(pid):
            fh.close()
            return {
                "pid": pid,
                "port": existing.get('port', port),
                "started": existing.get('started'),
            }
        # Lock held by a dead process — OS should have released it. Retry once.
        if not _try_file_lock(fh):
            fh.close()
            return {"pid": pid, "port": port, "stale": True}
    fh.seek(0)
    fh.truncate()
    fh.write(json.dumps({
        "pid": os.getpid(),
        "port": port,
        "started": time.time(),
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
        except FileNotFoundError:
            pass
        except Exception:
            pass
        _lock_path = None


def _no_sim():
    return {"error": "Simulator not running. Call sim_start first."}


# ── Lifecycle tools ──────────────────────────────────────────────────────────

def _load_wheel_sim():
    """Import wheel_sim.py from the same directory."""
    import importlib.util
    _ws_path = Path(__file__).parent / 'wheel_sim.py'
    _spec = importlib.util.spec_from_file_location('wheel_sim', _ws_path)
    _ws = importlib.util.module_from_spec(_spec)
    _spec.loader.exec_module(_ws)
    return _ws


def _apply_model(ws_mod, model_name: str) -> dict:
    """Switch wheel model: rebuild identity tables, device catalog, frame set.
    Serial + hw_id are randomised on every sim_start so PitHouse treats each
    session as a fresh wheel and doesn't cache-skip uploads against its
    per-device routing key (mcUid / serial)."""
    import copy
    import random
    import time as _time
    model = copy.deepcopy(ws_mod.WHEEL_MODELS[model_name])
    # Respect model's hardcoded serials and hw_id verbatim. Previously these
    # were randomised per sim_start to cache-bust PitHouse's dashboard sync,
    # but mismatch with session1_desc embedded values broke display
    # detection. When user wants exact capture-derived identity, model-
    # profile values must flow through unchanged.
    plugin_probe_rsp, pithouse_id_rsp, device_id_rsp = ws_mod._build_identity_tables(model)
    display_model_name = model.get('display', {}).get('name', '')
    # Also set module globals for backward compat with standalone mode
    ws_mod._PLUGIN_PROBE_RSP, ws_mod._PITHOUSE_ID_RSP = plugin_probe_rsp, pithouse_id_rsp
    ws_mod._DEVICE_ID_RSP = device_id_rsp
    ws_mod._DISPLAY_MODEL_NAME = display_model_name

    db = _config.get('db', {})
    channel_urls = [v['url'] for v in db.values()] if db else []

    device_catalog = {}
    catalog_source = model.get('catalog_pcapng')
    if catalog_source:
        cap_path = Path(__file__).parent.parent / catalog_source
        if cap_path.exists():
            raw = ws_mod.extract_device_catalog(str(cap_path))
            device_catalog = {s: frs for s, frs in raw.items() if s in (0x01, 0x02)}
    if not device_catalog:
        device_catalog = ws_mod.build_device_catalog(model, channel_urls)

    frames_name = model.get('_7c23_frames_name', 'CSP')
    c7_23_frames = {'CSP': ws_mod._7C_23_FRAMES_CSP, 'VGS': ws_mod._7C_23_FRAMES_VGS}.get(
        frames_name, ws_mod._7C_23_FRAMES_CSP)

    # Per-model replay table: layer JSON tables over any startup-loaded pcap
    # replay. The fallback to `_config['replay']` (loaded once from default
    # pcap) stays in place for models without their own JSON tables.
    replay_override = None
    tables = model.get('replay_tables') or []
    if tables:
        replay_override = ws_mod.ResponseReplay()
        for rel in tables:
            abs_path = Path(__file__).parent.parent / rel
            if not abs_path.exists():
                print(f'[WARN] replay_table {rel} not found', file=sys.stderr)
                continue
            added = replay_override.load_json(str(abs_path))
            print(f'[replay: +{added} entries from {rel}]', file=sys.stderr)

    return {
        'model': model,
        'device_catalog': device_catalog,
        'emits_7c23': bool(model.get('emits_7c23', True)),
        'c7_23_frames': c7_23_frames,
        'c7_23_reps': int(model.get('_7c23_reps', 13)),
        'plugin_probe_rsp': plugin_probe_rsp,
        'pithouse_id_rsp': pithouse_id_rsp,
        'device_id_rsp': device_id_rsp,
        'display_model_name': display_model_name,
        'replay': replay_override,
    }


@_server.tool()
def sim_start(port: Optional[str] = None, model: Optional[str] = None) -> dict:
    """Start the simulator on a serial port. Uses configured port/model if omitted.
    Model choices: vgs, csp, ks."""
    global _sim, _session, _last_disconnect

    if _session is not None:
        return {"error": "Simulator already running", "port": _config.get('port', '')}

    if _last_disconnect > 0:
        remaining = _COOLDOWN_SEC - (time.monotonic() - _last_disconnect)
        if remaining > 0:
            time.sleep(remaining)

    if not _config:
        return {"error": "MCP server not configured. Was --mcp used with wheel_sim.py?"}

    use_port = port or _config.get('port', '')
    if not use_port:
        return {"error": "No port specified"}

    # Cross-process port lock. Blocks a second wheel_sim (any MCP server,
    # any Claude session) from binding the same port. Released on sim_stop
    # or process death.
    conflict = _acquire_port_lock(use_port)
    if conflict:
        return {
            "error": f"Port {use_port} already in use by another wheel_sim (pid {conflict.get('pid')})",
            "owner": conflict,
            "hint": "Call sim_stop from any session to kill the owner.",
        }

    try:
        import serial
    except ImportError:
        _release_port_lock()
        return {"error": "pyserial not installed"}

    try:
        ser = serial.Serial(use_port, baudrate=115200, timeout=None)
    except (serial.SerialException, OSError) as e:
        _release_port_lock()
        return {"error": f"Cannot open {use_port}: {e}"}

    _ws = _load_wheel_sim()

    # Apply model (switch identity tables + rebuild catalog if needed)
    if model:
        if model not in _ws.WHEEL_MODELS:
            ser.close()
            _release_port_lock()
            return {"error": f"Unknown model '{model}'. Available: {sorted(_ws.WHEEL_MODELS.keys())}"}
        overrides = _apply_model(_ws, model)
    else:
        overrides = {}
        # Apply default model identity tables
        default_model = _config.get('model', {})
        if default_model:
            plugin_probe_rsp, pithouse_id_rsp, device_id_rsp = _ws._build_identity_tables(default_model)
            display_model_name = default_model.get('display', {}).get('name', '')
            _ws._PLUGIN_PROBE_RSP, _ws._PITHOUSE_ID_RSP = plugin_probe_rsp, pithouse_id_rsp
            _ws._DEVICE_ID_RSP = device_id_rsp
            _ws._DISPLAY_MODEL_NAME = display_model_name
            overrides['plugin_probe_rsp'] = plugin_probe_rsp
            overrides['pithouse_id_rsp'] = pithouse_id_rsp
            overrides['device_id_rsp'] = device_id_rsp
            overrides['display_model_name'] = display_model_name

    use_device_catalog = overrides.get('device_catalog', _config['device_catalog'])
    use_emits_7c23 = overrides.get('emits_7c23', _config['emits_7c23'])
    use_c7_23_frames = overrides.get('c7_23_frames', _config['c7_23_frames'])
    use_c7_23_reps = overrides.get('c7_23_reps', _config['c7_23_reps'])
    use_model = overrides.get('model', _config.get('model', {}))
    use_replay = overrides.get('replay') or _config.get('replay')

    log_path = Path(__file__).parent / 'logs' / 'wheel_sim.log'
    log_fh = _ws._open_session_log(log_path, use_port)
    model_name = use_model.get('friendly', use_model.get('name', 'unknown'))
    print(f'[MCP sim_start] model={model_name} port={use_port}', file=sys.stderr)

    sim = _ws.WheelSimulator(
        _config['db'],
        use_replay,
        use_device_catalog,
        plugin_probe_rsp=overrides.get('plugin_probe_rsp'),
        pithouse_id_rsp=overrides.get('pithouse_id_rsp'),
        device_id_rsp=overrides.get('device_id_rsp'),
        display_model_name=overrides.get('display_model_name', ''),
        rpm_led_count=int(use_model.get('rpm_led_count', 10)),
        button_led_count=int(use_model.get('button_led_count', 14)),
    )
    _sim = sim

    alive = threading.Event()
    alive.set()
    write_lock = threading.Lock()

    session = _SimSession(
        ser, sim, log_fh, alive, write_lock,
        use_emits_7c23,
        use_c7_23_frames,
        use_c7_23_reps,
    )
    session.start()
    _session = session

    return {"status": "running", "port": use_port, "model": model_name}


@_server.tool()
def sim_stop() -> dict:
    """Stop the simulator and close the serial port.
    If the sim is owned by a different process (another MCP server / Claude
    session), sends SIGTERM/taskkill to the owner PID recorded in the port
    lockfile."""
    global _sim, _session, _last_disconnect

    if _session is not None:
        _session.stop()
        _session = None
        _sim = None
        _last_disconnect = time.monotonic()
        _release_port_lock()
        return {"status": "stopped"}

    # No local session — check the configured port's lockfile for a
    # cross-process owner and signal it.
    use_port = _config.get('port', '')
    if not use_port:
        return {"error": "Simulator not running"}
    path = _port_lock_path(use_port)
    if not path.exists():
        return {"error": "Simulator not running"}
    info = _read_lockfile(path)
    pid = int(info.get('pid', 0) or 0)
    if not pid or not _pid_alive(pid):
        try:
            path.unlink()
        except Exception:
            pass
        return {"error": "Simulator not running"}
    if pid == os.getpid():
        # This process holds the lock but has no session — inconsistent state.
        _release_port_lock()
        return {"status": "stopped", "note": "cleared stale local lock"}

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
        "status": "stopped" if not still_alive else "signal_sent",
        "cross_process": True,
        "killed_pid": pid,
        "owner_port": info.get('port'),
    }


@_server.tool()
def sim_reload() -> dict:
    """Reload wheel_sim.py from disk, picking up code changes. Stops session if running.
    Call sim_start after to reconnect with fresh code."""
    global _sim, _session, _last_disconnect

    stopped = False
    if _session is not None:
        _session.stop()
        _session = None
        _sim = None
        _last_disconnect = time.monotonic()
        _release_port_lock()
        stopped = True

    # Purge cached wheel_sim module so next _load_wheel_sim() gets fresh code
    import sys as _sys
    _sys.modules.pop('wheel_sim', None)

    return {"status": "reloaded", "session_stopped": stopped}


@_server.tool()
def sim_info() -> dict:
    """Connection info: running state, port."""
    running = _session is not None
    return {"running": running, "port": _config.get('port', '')}


# ── Query tools ──────────────────────────────────────────────────────────────

@_server.tool()
def sim_status() -> dict:
    """Current simulator state: sessions, tier def, display, uptime, frame counts, fps."""
    if _sim is None:
        return _no_sim()
    return {
        "uptime_s": round(_sim.uptime, 1),
        "sessions_opened": _sim.sessions_opened,
        "mgmt_session": f"0x{_sim.mgmt_session:02X}" if _sim.mgmt_session else None,
        "telem_session": f"0x{_sim.telem_session:02X}" if _sim.telem_session else None,
        "tier_def_received": _sim.tier_def_received,
        "display_detected": _sim.display_detected,
        "frames_total": _sim.frames_total,
        "frames_telem": _sim.frames_telem,
        "replay_hits": _sim.replay_hits,
        "unhandled_total": _sim.unhandled_total,
        "unhandled_unique": len(_sim.unhandled_counts),
        "catalog_sent": _sim.catalog_sent,
        "proactive_sent": _sim.proactive_sent,
        "fps": round(_sim.fps, 1),
        "session_data_counts": {
            f"0x{s:02X}": n for s, n in sorted(_sim.session_data_counts.items())
        },
    }


@_server.tool()
def sim_telemetry(channel: Optional[str] = None) -> dict:
    """Current decoded telemetry values. Pass channel name to filter, or omit for all."""
    if _sim is None:
        return _no_sim()
    values = dict(_sim.values)
    if channel:
        filtered = {k: v for k, v in values.items() if channel.lower() in k.lower()}
        return filtered if filtered else {"error": f"No channel matching '{channel}'"}
    for k, v in values.items():
        if isinstance(v, float):
            values[k] = round(v, 4) if v == v else None
    return values


@_server.tool()
def sim_channels() -> list:
    """List all tier-defined channels with compression type and bit width."""
    if _sim is None:
        return _no_sim()
    return [
        {
            "name": c["name"],
            "compression": c.get("compression", ""),
            "bit_width": c.get("bit_width", 0),
        }
        for c in _sim.channels
    ]


@_server.tool()
def sim_unhandled() -> dict:
    """Unhandled frame types with counts and labels."""
    if _sim is None:
        return _no_sim()
    items = []
    for (g, d, cmd), count in sorted(
        _sim.unhandled_counts.items(), key=lambda x: -x[1]
    ):
        label = _sim.unhandled_labels.get((g, d, cmd), "")
        items.append({
            "group": f"0x{g:02X}",
            "device": f"0x{d:02X}",
            "cmd": cmd,
            "count": count,
            "label": label,
        })
    return {"total": _sim.unhandled_total, "unique": len(items), "items": items}


@_server.tool()
def sim_recent(count: int = 10, tag: Optional[str] = None,
               exclude: Optional[str] = None) -> list:
    """Recent frames from rolling log (tag + hex). Buffer holds up to 2000.
    Pass tag="display_cfg" to filter to one tag, or tag="display_cfg,session_data"
    for multiple. Pass exclude="replay,heartbeat" to drop noisy tags."""
    if _sim is None:
        return _no_sim()
    frames = list(_sim.recent_frames)
    if tag:
        wanted = {t.strip() for t in tag.split(',')}
        frames = [(t, h) for t, h in frames if t in wanted]
    if exclude:
        skip = {t.strip() for t in exclude.split(',')}
        frames = [(t, h) for t, h in frames if t not in skip]
    count = min(count, len(frames))
    return [{"tag": t, "hex": h} for t, h in frames[:count]]


@_server.tool()
def sim_counters() -> dict:
    """Per-category frame counts (session, telemetry, replay, unhandled, etc.)."""
    if _sim is None:
        return _no_sim()
    result = dict(_sim.cat_counts)
    result["total"] = _sim.frames_total
    result["proactive_sent"] = _sim.proactive_sent
    result["session_data_counts"] = {
        f"0x{s:02X}": n for s, n in sorted(_sim.session_data_counts.items())
    }
    return result


@_server.tool()
def sim_uploads() -> dict:
    """What PitHouse has uploaded: decoded zlib blobs + extracted dashboard
    metadata. Each blob carries size, session, and (if parseable) JSON root
    keys or a UTF-16 preview.

    Research fields (S1/S3):
      envelope_hex — up to 64 bytes preceding the zlib magic (absolute)
      envelope_from_prev_hex — bytes since previous blob's zlib end in same
        session; the per-blob framing envelope under investigation
      compressed_size — zlib stream byte length
      tile_server — structured view of tile-server state blobs (session 0x03):
        per-game populated/empty + name + layers_count + bounds"""
    if _sim is None:
        return _no_sim()
    ut = getattr(_sim, '_upload_tracker', None)
    if ut is None:
        return {"error": "Upload tracker not available"}
    blobs = []
    for b in ut.decoded_blobs:
        item = {
            "session": f"0x{b['session']:02x}",
            "size": b['size'],
            "offset": b['session_offset'],
            "compressed_size": b.get('compressed_size'),
            "envelope_from_prev_hex": b.get('envelope_from_prev_hex', ''),
            "envelope_from_prev_len": b.get('envelope_from_prev_len', 0),
            "envelope_hex": b.get('envelope_hex', ''),
        }
        if b.get('json') is not None:
            if isinstance(b['json'], dict):
                item["json_keys"] = list(b['json'].keys())
            item["json_preview"] = str(b['json'])[:500]
        elif b.get('utf16'):
            item["utf16_preview"] = b['utf16'][:200]
        if b.get('tile_server') is not None:
            item["tile_server"] = b['tile_server']
        blobs.append(item)
    # S1 helper: constant-byte analysis across blobs with identical envelope
    # length. Marks bytes that never change with '==' and varying bytes with
    # '??' to speed hypothesis formation.
    envelope_diff = _analyse_envelope_diff(ut.decoded_blobs)
    return {
        "blobs": blobs,
        "dashboards": ut.uploaded_dashboards,
        "envelope_diff": envelope_diff,
    }


def _analyse_envelope_diff(blobs) -> dict:
    """Group blobs by (session, envelope_from_prev_len) and report which bytes
    are constant across blobs within each group. Constant bytes are shown as
    hex; variable bytes are shown as '??'."""
    from collections import defaultdict
    groups: dict = defaultdict(list)
    for b in blobs:
        env = b.get('envelope_from_prev_hex', '')
        if not env:
            continue
        groups[(b['session'], len(env) // 2)].append(bytes.fromhex(env))
    out = {}
    for (session, length), envs in groups.items():
        if len(envs) < 2:
            continue
        pattern = []
        for i in range(length):
            first = envs[0][i]
            if all(e[i] == first for e in envs):
                pattern.append(f'{first:02x}')
            else:
                pattern.append('??')
        out[f"0x{session:02x}_len{length}"] = {
            "count": len(envs),
            "pattern": ' '.join(pattern),
        }
    return out


@_server.tool()
def sim_rpc_log() -> list:
    """JSON RPCs parsed from PitHouse uploads (session 0x0a primarily).
    Each entry: session, method, arg, id, ts. Dashboard delete, select,
    and other wheel-state mutations arrive here."""
    if _sim is None:
        return _no_sim()
    ut = getattr(_sim, '_upload_tracker', None)
    if ut is None:
        return []
    return [
        {
            'session': f"0x{e['session']:02x}",
            'method': e['method'],
            'arg': e['arg'],
            'id': e['id'],
            'ts': e['ts'],
        }
        for e in ut.rpc_log
    ]


@_server.tool()
def sim_fs_tree(path: Optional[str] = None) -> dict:
    """Snapshot of the simulated wheel filesystem. Default returns all files
    (path → size/md5/mtime). Pass `path` to limit to a subtree prefix (e.g.
    "/home/root/resource/dashes"). Uploads write here; deletes (via
    completelyRemove RPC) remove from here."""
    if _sim is None:
        return _no_sim()
    fs = getattr(_sim, 'fs', None)
    if fs is None:
        return {"error": "Filesystem not initialized"}
    tree = fs.tree()
    if path:
        norm = path.rstrip('/') or '/'
        tree = {p: m for p, m in tree.items()
                if p == norm or p.startswith(norm + '/')}
    return {"files": tree, "count": len(tree)}


@_server.tool()
def sim_stored_dashboards() -> list:
    """Current simulated wheel-stored dashboard list. Reflects uploads,
    deletions, and selections observed on the wire. Persisted across
    sim restarts at sim/logs/stored_dashboards.json."""
    if _sim is None:
        return _no_sim()
    return [
        {
            'title': d.get('title', ''),
            'dirName': d.get('dirName', ''),
            'id': d.get('id', ''),
            'hash': d.get('hash', '')[:16] + '…' if d.get('hash') else '',
            'size': d.get('_mzdash_size'),
        }
        for d in _sim.stored_dashboards
    ]


@_server.tool()
def sim_reset_fs(install_stub: Optional[str] = None) -> dict:
    """Clear the simulated wheel filesystem + stored dashboards + upload
    tracker state. Forces configJson state to re-emit as empty so PitHouse
    UI sees "no dashboards installed" and issues real upload RPCs.

    Args:
      install_stub: Optional dashboard name to install as a single stub
        dashboard (e.g. "Core"). If set, the matching entry from the
        captured factory state is restored to the virtual FS with realistic
        hash/id/metadata so PitHouse sees one-installed-dashboard state.
        Omit or set None for a truly empty wheel."""
    if _sim is None:
        return _no_sim()
    fs = getattr(_sim, 'fs', None)
    if fs is None:
        return {"error": "Filesystem not initialized"}
    # Wipe FS — factory-fresh state. User uploads grow it from here.
    try:
        fs._files.clear()
        fs._save()
    except Exception as e:
        return {"error": f"FS clear failed: {e}"}
    installed: Optional[dict] = None
    if install_stub:
        try:
            installed = fs.populate_single_stub_dashboard(install_stub)
        except Exception as e:
            return {"error": f"stub install failed: {e}"}
    # Reset stored dashboards
    try:
        _sim.stored_dashboards = []
    except Exception:
        pass
    # Clear upload tracker so re-pushed blobs re-decode
    ut = getattr(_sim, '_upload_tracker', None)
    if ut is not None:
        ut.decoded_blobs = []
        ut.uploaded_dashboards = []
        ut.rpc_log = []
        ut._bufs = {}
        ut._prev_blob_end = {}
    # Reset session data counts
    _sim.session_data_counts = {}
    result = {"status": "reset", "fs_count": len(fs.tree())}
    if installed:
        result["installed_stub"] = {
            "dirName": installed.get('dirName', ''),
            "title": installed.get('title', ''),
            "id": installed.get('id', ''),
        }
    return result


@_server.tool()
def sim_push_configjson(use_factory: bool = True) -> dict:
    """Queue a session 0x09 configJson state push on the device→host side.
    Frames accumulate in `_pending_sends` and flush on the next host-triggered
    handle() call (PitHouse heartbeats fire handle() frequently so the replay
    typically lands within a few ms).

    Args:
      use_factory: True (default) serializes the captured real-wheel factory
        state verbatim. False uses the sim-built state derived from current
        FS dashboards."""
    if _sim is None:
        return _no_sim()
    try:
        n = _sim.push_configjson_replay(use_factory=use_factory)
    except Exception as e:
        return {"error": f"push failed: {e}"}
    return {
        "status": "queued",
        "frames": n,
        "use_factory": use_factory,
        "next_seq": f"0x{getattr(_sim, '_session09_next_seq', 0):04x}",
    }


@_server.tool()
def sim_reported_state() -> dict:
    """What the sim would emit to PitHouse via session 0x09 configJson state
    right now, based on current FS. Useful for spotting discrepancies between
    FS contents and configJsonList (the list-of-names field that PitHouse's
    UI uses for cache-skip decisions)."""
    if _sim is None:
        return _no_sim()
    fs = getattr(_sim, 'fs', None)
    if fs is None:
        return {"error": "Filesystem not initialized"}
    try:
        import wheel_sim as _ws
        import zlib
        import json as _json
        dashboards = fs.dashboards()
        # Build the actual state bytes the sim would send to PitHouse, then
        # decode + expose the full structure. This reflects the factory-state
        # merge (11 canonical + FS uploads) rather than just FS contents.
        payload = _ws.build_configjson_state(dashboards)
        state_json = _json.loads(zlib.decompress(payload[9:]))
        em = state_json.get('enableManager', {}).get('dashboards', [])
        em_names = [d.get('dirName') or d.get('title') or d.get('name', '') for d in em]
        return {
            "configJsonList": state_json.get('configJsonList', []),
            "configJsonList_count": len(state_json.get('configJsonList', [])),
            "enableManager_dashboards_count": len(em),
            "enableManager_dashboard_names": em_names,
            "displayVersion": state_json.get('displayVersion'),
            "resetVersion": state_json.get('resetVersion'),
            "rootDirPath": state_json.get('rootDirPath'),
            "top_level_keys": list(state_json.keys()),
            "fs_count": len(fs.tree()),
            "fs_dashboards_count": len(dashboards),
        }
    except Exception as e:
        return {"error": str(e)}


def run_stdio():
    """Run the MCP server on stdio (blocking)."""
    _server.run(transport="stdio")
