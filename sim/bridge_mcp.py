"""MCP server for the MOZA USB-MITM bridge.

Wraps `bridge.BridgeEngine` so Claude Code can drive the passthrough lifecycle
(real-base ↔ gadget pump) and inspect captured frames live.

Stdio MCP server. Configure once via `configure()` (called from
`bridge.py --mcp`) before starting.
"""
from __future__ import annotations

import os
import platform
import re
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Optional

try:
    import fcntl as _fcntl
    _HAS_FCNTL = True
except ImportError:
    _HAS_FCNTL = False
    import msvcrt as _msvcrt

from mcp.server.fastmcp import FastMCP

sys.path.insert(0, str(Path(__file__).parent))
from bridge import (  # type: ignore
    BASE_TO_HOST,
    HOST_TO_BASE,
    get_engine,
)
from wheel_sim import frame_payload, verify  # type: ignore

_server = FastMCP("bridge")
_IS_WINDOWS = platform.system() == "Windows"

_config: dict = {}
_lock_fh = None
_lock_path: Optional[Path] = None


def configure(*, default_base: Optional[str], default_gadget: str, baud: int) -> None:
    _config.update({
        "default_base": default_base,
        "default_gadget": default_gadget,
        "baud": baud,
    })


# ── Cross-process gadget lock ───────────────────────────────────────────────
# Bridge and wheel_sim both want /dev/ttyGS0. Share the lockfile naming with
# mcp_server.py (`wheel_sim_<slug>.lock`) so a wheel_sim already holding the
# gadget blocks bridge_start, and vice versa.

def _port_lock_path(port: str) -> Path:
    slug = re.sub(r"[^A-Za-z0-9]+", "_", port).strip("_") or "default"
    return Path(tempfile.gettempdir()) / f"wheel_sim_{slug}.lock"


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
        import json
        return json.loads(path.read_text())
    except Exception:
        return {}


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if _IS_WINDOWS:
        out = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/NH"],
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
        subprocess.run(["taskkill", "/F", "/PID", str(pid)],
                       capture_output=True, check=False)
    else:
        import signal as _signal
        try:
            os.kill(pid, _signal.SIGTERM)
        except ProcessLookupError:
            pass


def _acquire_port_lock(port: str) -> Optional[dict]:
    global _lock_fh, _lock_path
    import json
    path = _port_lock_path(port)
    fh = open(path, "a+")
    if not _try_file_lock(fh):
        existing = _read_lockfile(path)
        pid = int(existing.get("pid", 0) or 0)
        if pid and _pid_alive(pid):
            fh.close()
            return {"pid": pid, "port": existing.get("port", port),
                    "started": existing.get("started")}
        if not _try_file_lock(fh):
            fh.close()
            return {"pid": pid, "port": port, "stale": True}
    fh.seek(0)
    fh.truncate()
    fh.write(json.dumps({"pid": os.getpid(), "port": port,
                         "started": time.time()}))
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


def _no_engine() -> dict:
    return {"error": "Bridge not running. Call bridge_start first."}


# ── Lifecycle tools ─────────────────────────────────────────────────────────

@_server.tool()
def bridge_start(base_port: Optional[str] = None,
                 gadget_port: Optional[str] = None,
                 log_path: Optional[str] = None) -> dict:
    """Start the passthrough bridge. base_port is the real MOZA base CDC ACM
    device (e.g. /dev/ttyACM0); gadget_port defaults to /dev/ttyGS0. The
    libcomposite gadget must already be set up via setup_usbip_gadget.sh."""
    use_base = base_port or _config.get("default_base")
    use_gadget = gadget_port or _config.get("default_gadget", "/dev/ttyGS0")
    if not use_base:
        return {"error": "base_port is required (no default configured)"}
    engine = get_engine()
    if engine.is_running():
        return {"error": "bridge already running",
                "base_port": engine.base_port,
                "gadget_port": engine.gadget_port}
    conflict = _acquire_port_lock(use_gadget)
    if conflict:
        # Self-heal: lock held by our own pid means a previous engine.start
        # crashed mid-flight (pumps died on USBIP detach, lockfile lingered).
        # Drop the stale fh and retry once.
        if conflict.get("pid") == os.getpid():
            _release_port_lock()
            conflict = _acquire_port_lock(use_gadget)
    if conflict:
        return {
            "error": f"Gadget port {use_gadget} held by another bridge (pid {conflict.get('pid')})",
            "owner": conflict,
            "hint": "Call bridge_stop from any session to kill the owner.",
        }
    res = engine.start(use_base, use_gadget,
                       baud=_config.get("baud", 115200),
                       log_path=Path(log_path) if log_path else None)
    if "error" in res:
        _release_port_lock()
    return res


@_server.tool()
def bridge_stop() -> dict:
    """Stop the bridge. If a different process owns the gadget lock, sends
    SIGTERM to that pid (mirrors wheel_sim's cross-process stop semantics)."""
    engine = get_engine()
    if engine.is_running():
        res = engine.stop()
        _release_port_lock()
        return res

    use_gadget = _config.get("default_gadget", "/dev/ttyGS0")
    path = _port_lock_path(use_gadget)
    if not path.exists():
        return {"error": "bridge not running"}
    info = _read_lockfile(path)
    pid = int(info.get("pid", 0) or 0)
    if not pid or not _pid_alive(pid):
        try:
            path.unlink()
        except Exception:
            pass
        return {"error": "bridge not running"}
    if pid == os.getpid():
        _release_port_lock()
        return {"status": "stopped", "note": "cleared stale local lock"}
    _kill_pid(pid)
    deadline = time.monotonic() + 3.0
    while time.monotonic() < deadline and _pid_alive(pid):
        time.sleep(0.1)
    still = _pid_alive(pid)
    if not still:
        try:
            path.unlink()
        except Exception:
            pass
    return {
        "status": "stopped" if not still else "signal_sent",
        "cross_process": True,
        "killed_pid": pid,
        "owner_port": info.get("port"),
    }


@_server.tool()
def bridge_info() -> dict:
    """Configured ports + current running state. Cheap."""
    engine = get_engine()
    return {
        "running": engine.is_running(),
        "base_port": engine.base_port or _config.get("default_base"),
        "gadget_port": engine.gadget_port or _config.get("default_gadget"),
        "log_path": str(engine.log_path) if engine.log_path else None,
    }


@_server.tool()
def bridge_status() -> dict:
    """Frame counts, byte counts, uptime, current ports/log path."""
    engine = get_engine()
    if not engine.is_running() and engine.logger is None:
        return _no_engine()
    return engine.status()


@_server.tool()
def bridge_recent(count: int = 20, direction: Optional[str] = None) -> list:
    """Last N frames from the rolling buffer (cap 2000).

    direction: 'h2b' for PitHouse→base, 'b2h' for base→PitHouse, omit for both.
    Each item carries timestamp, direction, group, device, payload, and raw
    hex. ok=False means checksum failed."""
    engine = get_engine()
    if engine.logger is None:
        return _no_engine()
    items = engine.logger.recent_snapshot(count, direction)
    out = []
    for t, d, hexstr in items:
        frame = bytes.fromhex(hexstr)
        ok = verify(frame)
        rec = {"t": t, "dir": d, "len": len(frame), "ok": ok, "hex": hexstr}
        if len(frame) >= 4 and ok:
            rec["grp"] = f"0x{frame[2]:02X}"
            rec["dev"] = f"0x{frame[3]:02X}"
            rec["payload"] = frame_payload(frame).hex()
        out.append(rec)
    return out


@_server.tool()
def bridge_histogram(top: int = 50) -> list:
    """Per-shape frame count histogram. Each entry: direction, group, device,
    cmd-prefix (first 2 payload bytes), count. Sorted desc by count.
    `top` caps the result list (0 for all)."""
    engine = get_engine()
    if engine.logger is None:
        return _no_engine()
    items = engine.logger.histogram_snapshot()
    out = []
    for (direction, grp, dev, cmd), count in items:
        rec = {
            "dir": direction,
            "count": count,
        }
        if grp >= 0:
            rec["grp"] = f"0x{grp:02X}"
            rec["dev"] = f"0x{dev:02X}"
            rec["cmd"] = cmd
        else:
            rec["bad_checksum"] = True
        out.append(rec)
    if top and top > 0:
        out = out[:top]
    return out


@_server.tool()
def bridge_counters() -> dict:
    """Aggregate counters: frames per direction, bytes per direction,
    bad-checksum count, total frames, uptime."""
    engine = get_engine()
    if engine.logger is None:
        return _no_engine()
    snap = engine.logger.snapshot()
    return {
        "frames": snap["frames"],
        "bad_checksum": snap["bad_checksum"],
        "uptime_s": snap["uptime_s"],
        "by_direction": {
            "h2b": {
                "frames": snap["counts_by_dir"].get(HOST_TO_BASE, 0),
                "bytes": snap["bytes_by_dir"].get(HOST_TO_BASE, 0),
            },
            "b2h": {
                "frames": snap["counts_by_dir"].get(BASE_TO_HOST, 0),
                "bytes": snap["bytes_by_dir"].get(BASE_TO_HOST, 0),
            },
        },
    }


@_server.tool()
def bridge_reset_counters() -> dict:
    """Zero all counters and clear the recent ring buffer. Persistent JSONL
    log on disk is untouched."""
    engine = get_engine()
    if engine.logger is None:
        return _no_engine()
    engine.logger.reset_counters()
    return {"status": "reset"}


@_server.tool()
def bridge_log_path() -> dict:
    """Absolute path of the current JSONL trace, plus its size in bytes."""
    engine = get_engine()
    if engine.log_path is None:
        return _no_engine()
    p = engine.log_path
    try:
        size = p.stat().st_size
    except OSError:
        size = None
    return {"path": str(p), "size": size}


def run_stdio() -> None:
    _server.run(transport="stdio")
