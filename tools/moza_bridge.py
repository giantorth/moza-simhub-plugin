"""Shared loader and decoder for PitHouse bridge-capture JSONL files.

Bridge captures are raw serial frames captured from PitHouse ↔ wheel
communication. Format per line:
  {"t": <epoch>, "dir": "h2b"|"b2h", "len": N, "ok": bool,
   "hex": "<raw frame>", "grp": N, "dev": N, "payload": "<hex>"}

Usage from other tools:
    from moza_bridge import load_bridge, BFrame
"""
import json
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

BRIDGE_DIR = Path(__file__).resolve().parent.parent / "sim" / "logs"


@dataclass
class BFrame:
    t: float          # absolute epoch timestamp
    t_rel: float      # seconds relative to trace start
    dir: str          # 'h2b' or 'b2h'
    grp: int
    dev: int
    payload: bytes    # payload portion of the frame (after grp+dev, before checksum)
    raw: bytes        # full frame bytes

    @property
    def is_telemetry(self) -> bool:
        return self.grp in (0x43, 0xC3)

    @property
    def prefix(self) -> int:
        return self.payload[0] if self.payload else -1

    @property
    def is_session_data(self) -> bool:
        return self.is_telemetry and len(self.payload) >= 2 and self.payload[0] == 0x7C

    @property
    def is_value_frame(self) -> bool:
        return self.is_telemetry and len(self.payload) >= 2 and self.payload[0] == 0x7D

    @property
    def is_flow_control(self) -> bool:
        return self.is_telemetry and len(self.payload) >= 2 and self.payload[0] == 0xFC

    @property
    def is_command(self) -> bool:
        """Non-session, non-VF, non-FC frame on telemetry group — direct command."""
        if not self.is_telemetry:
            return False
        if not self.payload:
            return False
        return self.payload[0] not in (0x7C, 0x7D, 0xFC)

    # Session-layer fields (valid when is_session_data)
    @property
    def sess_id(self) -> int:
        if not self.is_session_data or len(self.payload) < 4:
            return -1
        # payload: 7c <fixed_00> <sess_id> <stype> ...
        # Actually the format seems to be 7c <byte> ... where byte encodes session
        # Let me check: 7c 00 02 01 ...  → sess=0x02, stype=0x01?
        # From moza_trace.py h2b: 7c 00 <session> <stype> ...
        return self.payload[2] if len(self.payload) > 2 else -1

    @property
    def sess_type(self) -> int:
        if not self.is_session_data or len(self.payload) < 4:
            return -1
        return self.payload[3]

    @property
    def sess_seq(self) -> int:
        if not self.is_session_data or len(self.payload) < 6:
            return -1
        if self.sess_type not in (0x01, 0x00):
            return -1
        return self.payload[4] | (self.payload[5] << 8)

    @property
    def sess_data(self) -> bytes:
        if not self.is_session_data or self.sess_type != 0x01:
            return b''
        return self.payload[6:] if len(self.payload) > 6 else b''

    # Value frame fields
    @property
    def vf_flag(self) -> int:
        """Value frame flag byte (the tier/broadcast selector)."""
        if not self.is_value_frame:
            return -1
        # payload: 7d 23 <??> <??> <??> <??> <flag> <??> <data...>
        # From moza_trace.py: raw[4]=7d raw[5]=23, flag=raw[10], data=raw[12:]
        # In payload terms (starting after grp+dev): payload[0]=7d, [1]=23
        # flag offset = 10-4 = 6 from payload start
        if len(self.payload) < 7:
            return -1
        return self.payload[6]

    @property
    def vf_data(self) -> bytes:
        if not self.is_value_frame or len(self.payload) < 9:
            return b''
        return self.payload[8:]

    # FF-record detection in session data chunks
    @property
    def ff_kind(self) -> int:
        data = self.sess_data
        if len(data) > 13 and data[0] == 0xFF:
            return struct.unpack_from('<I', data, 9)[0]
        return -1

    @property
    def ff_size(self) -> int:
        data = self.sess_data
        if len(data) > 13 and data[0] == 0xFF:
            return struct.unpack_from('<I', data, 1)[0]
        return -1

    # FC ack fields
    @property
    def fc_session(self) -> int:
        if not self.is_flow_control or len(self.payload) < 3:
            return -1
        return self.payload[2]

    @property
    def fc_seq(self) -> int:
        if not self.is_flow_control or len(self.payload) < 5:
            return -1
        return self.payload[3] | (self.payload[4] << 8)


def load_bridge(path: str | Path, max_lines: int = 0) -> list[BFrame]:
    frames: list[BFrame] = []
    t0: Optional[float] = None
    with open(path) as fh:
        for i, line in enumerate(fh):
            if max_lines and i >= max_lines:
                break
            obj = json.loads(line.strip())
            t = obj["t"]
            if t0 is None:
                t0 = t
            raw = bytes.fromhex(obj["hex"])
            payload = bytes.fromhex(obj.get("payload", ""))
            frames.append(BFrame(
                t=t,
                t_rel=t - t0,
                dir=obj["dir"],
                grp=obj.get("grp", 0),
                dev=obj.get("dev", 0),
                payload=payload,
                raw=raw,
            ))
    return frames


def resolve_bridge(arg: Optional[str] = None) -> Path:
    if arg is None:
        candidates = sorted(BRIDGE_DIR.glob("bridge-*.jsonl"),
                            key=lambda p: p.stat().st_mtime, reverse=True)
        if candidates:
            return candidates[0]
        raise FileNotFoundError(f"No bridge captures in {BRIDGE_DIR}")
    p = Path(arg)
    if p.exists():
        return p
    candidates = sorted(BRIDGE_DIR.glob(f"*{arg}*"),
                        key=lambda x: x.stat().st_mtime, reverse=True)
    if candidates:
        return candidates[0]
    raise FileNotFoundError(f"No bridge capture matching '{arg}'")
