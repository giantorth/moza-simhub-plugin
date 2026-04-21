"""
MCP server for the MOZA Wheel Simulator.

Exposes simulator state as MCP tools so Claude Code can query telemetry,
session status, and protocol diagnostics. Also provides sim_start/sim_stop
tools to control the serial connection lifecycle.

Runs as a stdio MCP server. Configuration (port, model params, replay table,
device catalog) is passed in via configure() before starting.
"""

import json
import sys
import threading
import time
from pathlib import Path
from typing import Dict, List, Optional

from mcp.server.fastmcp import FastMCP

_server = FastMCP("wheel-sim")

# ── Lifecycle state ──────────────────────────────────────────────────────────

_sim = None           # WheelSimulator instance (created on sim_start)
_session = None       # _SimSession instance (serial + threads)
_config = {}          # Stored by configure()
_last_disconnect = 0.0  # monotonic timestamp of last sim_stop
_COOLDOWN_SEC = 5.0


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
    """Switch wheel model: rebuild identity tables, device catalog, frame set."""
    model = ws_mod.WHEEL_MODELS[model_name]
    plugin_probe_rsp, pithouse_id_rsp = ws_mod._build_identity_tables(model)
    display_model_name = model.get('display', {}).get('name', '')
    # Also set module globals for backward compat with standalone mode
    ws_mod._PLUGIN_PROBE_RSP, ws_mod._PITHOUSE_ID_RSP = plugin_probe_rsp, pithouse_id_rsp
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

    return {
        'model': model,
        'device_catalog': device_catalog,
        'emits_7c23': bool(model.get('emits_7c23', True)),
        'c7_23_frames': c7_23_frames,
        'c7_23_reps': int(model.get('_7c23_reps', 13)),
        'plugin_probe_rsp': plugin_probe_rsp,
        'pithouse_id_rsp': pithouse_id_rsp,
        'display_model_name': display_model_name,
    }


@_server.tool()
def sim_start(port: Optional[str] = None, model: Optional[str] = None) -> dict:
    """Start the simulator on a serial port. Uses configured port/model if omitted.
    Model choices: vgs, csp, ks. Enforces 5s cooldown after disconnect."""
    global _sim, _session, _last_disconnect

    if _session is not None:
        return {"error": "Simulator already running", "port": _config.get('port', '')}

    cooldown_remaining = _COOLDOWN_SEC - (time.monotonic() - _last_disconnect)
    if _last_disconnect > 0 and cooldown_remaining > 0:
        return {"error": f"Cooldown active. {cooldown_remaining:.1f}s remaining before reconnect allowed."}

    if not _config:
        return {"error": "MCP server not configured. Was --mcp used with wheel_sim.py?"}

    use_port = port or _config.get('port', '')
    if not use_port:
        return {"error": "No port specified"}

    try:
        import serial
    except ImportError:
        return {"error": "pyserial not installed"}

    try:
        ser = serial.Serial(use_port, baudrate=115200, timeout=None)
    except (serial.SerialException, OSError) as e:
        return {"error": f"Cannot open {use_port}: {e}"}

    _ws = _load_wheel_sim()

    # Apply model (switch identity tables + rebuild catalog if needed)
    if model:
        if model not in _ws.WHEEL_MODELS:
            ser.close()
            return {"error": f"Unknown model '{model}'. Available: {sorted(_ws.WHEEL_MODELS.keys())}"}
        overrides = _apply_model(_ws, model)
    else:
        overrides = {}
        # Apply default model identity tables
        default_model = _config.get('model', {})
        if default_model:
            plugin_probe_rsp, pithouse_id_rsp = _ws._build_identity_tables(default_model)
            display_model_name = default_model.get('display', {}).get('name', '')
            _ws._PLUGIN_PROBE_RSP, _ws._PITHOUSE_ID_RSP = plugin_probe_rsp, pithouse_id_rsp
            _ws._DISPLAY_MODEL_NAME = display_model_name
            overrides['plugin_probe_rsp'] = plugin_probe_rsp
            overrides['pithouse_id_rsp'] = pithouse_id_rsp
            overrides['display_model_name'] = display_model_name

    use_device_catalog = overrides.get('device_catalog', _config['device_catalog'])
    use_emits_7c23 = overrides.get('emits_7c23', _config['emits_7c23'])
    use_c7_23_frames = overrides.get('c7_23_frames', _config['c7_23_frames'])
    use_c7_23_reps = overrides.get('c7_23_reps', _config['c7_23_reps'])
    use_model = overrides.get('model', _config.get('model', {}))

    log_path = Path(__file__).parent / 'logs' / 'wheel_sim.log'
    log_fh = _ws._open_session_log(log_path, use_port)
    model_name = use_model.get('friendly', use_model.get('name', 'unknown'))
    print(f'[MCP sim_start] model={model_name} port={use_port}', file=sys.stderr)

    sim = _ws.WheelSimulator(
        _config['db'],
        _config['replay'],
        use_device_catalog,
        plugin_probe_rsp=overrides.get('plugin_probe_rsp'),
        pithouse_id_rsp=overrides.get('pithouse_id_rsp'),
        display_model_name=overrides.get('display_model_name', ''),
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
    """Stop the simulator and close the serial port. Starts 5s reconnect cooldown."""
    global _sim, _session, _last_disconnect

    if _session is None:
        return {"error": "Simulator not running"}

    _session.stop()
    _session = None
    _sim = None
    _last_disconnect = time.monotonic()

    return {"status": "stopped", "cooldown_sec": _COOLDOWN_SEC}


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
        stopped = True

    # Purge cached wheel_sim module so next _load_wheel_sim() gets fresh code
    import sys as _sys
    _sys.modules.pop('wheel_sim', None)

    return {"status": "reloaded", "session_stopped": stopped}


@_server.tool()
def sim_info() -> dict:
    """Connection info: running state, port, cooldown status."""
    running = _session is not None
    result = {"running": running, "port": _config.get('port', '')}
    if not running and _last_disconnect > 0:
        elapsed = time.monotonic() - _last_disconnect
        if elapsed < _COOLDOWN_SEC:
            result["cooldown_remaining"] = round(_COOLDOWN_SEC - elapsed, 1)
        else:
            result["cooldown_remaining"] = 0
    return result


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
    return result


@_server.tool()
def sim_uploads() -> dict:
    """What PitHouse has uploaded: decoded zlib blobs + extracted dashboard
    metadata. Each blob carries size, session, and (if parseable) JSON root
    keys or a UTF-16 preview."""
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
        }
        if b.get('json') is not None:
            if isinstance(b['json'], dict):
                item["json_keys"] = list(b['json'].keys())
            item["json_preview"] = str(b['json'])[:500]
        elif b.get('utf16'):
            item["utf16_preview"] = b['utf16'][:200]
        blobs.append(item)
    return {"blobs": blobs, "dashboards": ut.uploaded_dashboards}


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
    "/home/moza/resource/dashes"). Uploads write here; deletes (via
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


def run_stdio():
    """Run the MCP server on stdio (blocking)."""
    _server.run(transport="stdio")
