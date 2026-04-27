### Chunk format

Each `7c:00` data field contains one chunk:

```
session(1)  type(1)  seq_lo(1)  seq_hi(1)  payload(≤58)
```

| Field | Size | Description |
|-------|------|-------------|
| session | 1 | Session ID — pre-assigned, multiple concurrent |
| type | 1 | `0x01` = data, `0x00` = control/end marker, `0x81` = session channel open (device-initiated) |
| seq | 2 LE | Sequence number (monotonic within session) |
| payload | ≤58 | Net data per chunk; **all data chunks have 4-byte CRC-32 trailer** |

Net payload per full data chunk: **54 bytes** (58 minus 4-byte CRC). All data chunks include CRC-32 trailer, including final chunk.

### CRC algorithm

**Standard CRC-32** (ISO 3309 / ITU-T V.42, same as zlib/Ethernet/gzip/PNG):
- Polynomial: `0x04C11DB7` (reflected), init `0xFFFFFFFF`, xor-out `0xFFFFFFFF`
- Stored **little-endian** in 4-byte trailer
- Covers only **54-byte payload data** (excludes session/type/seq header)
- Per-chunk (not cumulative)
- Computable via `zlib.crc32(payload_bytes)` or `System.IO.Hashing.Crc32`

### Acknowledgments

`fc:00` with 3 bytes: `session(1) + ack_seq(2 LE)`. Session ID in ack identifies **ack sender's** session, not data sender's. Linked session pairs (e.g. 0x03↔0x0A) use cross-session acks.

**Session-open ACK must echo host's open_seq.** When host sends type=0x81 session open with `seq_lo:seq_hi`, wheel's `fc:00` ack must carry same seq value. Pithouse maintains monotonic port counter incrementing on each disconnect/reconnect; if wheel always replies with `ack_seq=0`, Pithouse treats as stale and retries endlessly (observed: 552 retries over 2.5 minutes). Counter starts at 1 on first power-on but increments across sessions.

### Session data chunk CRC — 4 bytes LE

**Verified 2026-04-24 (again).** Each session `7c:00` data chunk carries a **4-byte CRC32-LE** trailer over the net body. A previous revision of this section briefly claimed 3 bytes; that claim was an artifact of a buggy `extract_frames` helper that dropped the last 2 bytes of each frame (real CRC's last byte + frame checksum). When raw tshark output is inspected directly every chunk's last 4 bytes match `zlib.crc32(net)` LE exactly.

Full chunk wire layout: 6-byte `7c:00:sess:01:seq_lo:seq_hi` + 54-byte net data + 4-byte CRC32-LE = 64-byte payload = 69-byte frame (with `7e/N/group/device/cksum` framing, `N = 0x40`). The final chunk of a message is shorter; it still carries a 4-byte CRC over its (smaller) net data.

Sim chunking (`chunk_session_payload`, `_chunk_catalog_message`) and all chunk-CRC-aware ingestion paths (`UploadTracker.feed`, `PitHouseUploadReassembler.add`) use 4-byte CRC. `chunk_session_payload` exposes a `crc_bytes` knob for future firmware variants but defaults to 4.
