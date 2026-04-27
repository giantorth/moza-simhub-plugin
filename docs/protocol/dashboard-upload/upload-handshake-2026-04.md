### Upload protocol handshake sequence

> **2026-04+ firmware (current PitHouse).** Wheels: CSP on R9, KS Pro on R12. Capture: `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

Small-file flow (2025-11 firmware, `pithouse-switch-list-delete-upload-reupload.pcapng`, ~1.9KB mzdash):

1. Session 0x04 already open (from device_init). Host sends type=0x08 dir-listing probe with `/home/root`.
2. Wheel replies type=0x0a with populated directory listing.
3. Host sends `7c 23 46 80 08 00 06 00 fe 01` (session-open request, port=6).
4. Wheel emits device-initiated session-open for session 0x06 (`7c 00 06 81 06 00 06 00 fd 02`).
5. Host sends type=0x02 metadata on session 0x06 (316B for small file, 320B seen for 500KB).
6. Wheel replies type=0x01 ready-ack on session 0x06 (290B, echoes both path TLVs + md5 + size, bytes_written=0).
7. Host sends type=0x03 content on session 0x06 (one sub-msg, ~2192B for 1902B mzdash, contains zlib stream).
8. Wheel replies type=0x11 complete-ack (290B, bytes_written == total_size).
9. Host sends session_end `7c 00 06 00 ...`. Wheel sends session_end.

**Large-file flow (2026-04 firmware, observed 2026-04-24, ~500KB dashboard):** PitHouse splits the upload into many type=0x03 sub-msgs. Sim must emit a per-round progress ack or PitHouse stalls.

1–6. Same as small-file flow.
7. Host sends FIRST type=0x03 content sub-msg (size_field=4384, full path TLVs + 8B `compressed_header` + zlib data starting with `78 9c` magic).
8. Wheel emits type=0x01 progress ack with `bytes_written = decompressed_bytes_so_far`.
9. Host sends NEXT type=0x03 sub-msg (same paths echoed + raw deflate continuation, no `78 9c` magic at the same fixed offset within the msg).
10. Steps 8–9 repeat per round.
11. Once full zlib stream is reassembled and reaches deflate EOF, wheel emits type=0x11 complete-ack with `bytes_written = total_size`.
12. Host + wheel exchange session_end.

**Session number is dynamic.** Earlier docs hardcoded session `0x05` / `0x06`; in fresh 2026-04 PitHouse runs we have observed `0x07` carrying the upload (the `7c:23` trigger from the host picked port 7). Sim now treats any session in `0x04..0x0a` as a candidate file-transfer session and gates by buffer content (presence of a type=0x02 sub-msg).

**Device-side reply seq is independent from the host's seq counter on the same session.** Real wheel starts its file-transfer reply seq at `port + 1` (e.g. port 6 → first wheel→host data chunk at seq `0x07`). Sim previously reused the host's `_upload_next_seq` counter, which on a port-6 upload started replies at the host's last seq + 1 (≈ `0x11`); PitHouse silently dropped those out-of-window chunks. Fixed via `_ft_reply_next_seq[session]` initialised to `port + 1`.

**Per-round progress ack.** For each new type=0x03 round detected in the buffer, sim emits another type=0x01 with the latest `bytes_written` value. Without this, PitHouse halts the upload after the first round (the protocol behaves like a per-round flow-control credit). `_ft_rounds_acked[session]` tracks how many rounds have been acked so duplicate keepalive timers don't re-fire on the same round.

**Stuck-state recovery.** Sim reload (`mcp__wheel-sim-windows__sim_reload` then `sim_start`) appears to Windows as a USB disconnect → reconnect, forcing PitHouse to drop its cached "upload in progress" state and re-handshake cleanly. Required after any PitHouse retry-loop wedges (UI stuck on "resources syncing" with no wire activity).

### 1-byte XOR status after `ff*4` sentinel (not a 4-byte trailer)

**Resolved 2026-04-24.** The "4-byte trailer" chased in earlier revisions of this section was a misread. Only **1 byte** follows the `ff ff ff ff` sentinel. That byte is an **8-bit XOR checksum** over the body bytes — specifically, XOR of every byte from the first TLV marker through the final `ff` of the sentinel, producing a single byte appended as the message terminator. The 3 bytes that visually "looked like part of the trailer" in capture hex dumps were actually 3 of the 4 bytes of the chunk's 4-byte CRC32 — the last CRC byte and the frame checksum were getting silently dropped by a buggy capture-extract helper, making a 4+1 = 5-byte tail look like a 4-byte trailer.

Verified across every `type=0x01/0x02/0x03/0x08/0x0a/0x11` message with clean chunk CRCs in:

- `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` (both files, both directions)
- `09-04-26/dash-upload.pcapng` (legacy session 0x04 path)
- `12-04-26-2/moza-startup-1.pcapng` (handshake/telemetry)

For every message, `status == xor_over_body_bytes`. Example (file2 `type=0x01` ready-ack, 2025-11 capture): body XOR = `0x2e`, last byte on wire = `0x2e`.

**Message layout** (confirmed, replaces earlier speculation):

```
[type:1] [size_LE:u32] [pad:1]             — 6-byte header
[pad:2 = 00 00]                            — body begins here
[REMOTE path TLV]                          — 0x70 0x00 + UTF-16LE + 00 00
[LOCAL  path TLV]                          — 0x8A/0x8C 0x00 + UTF-16LE + 00 00  (firmware-dependent)
[flag:1 = 0x10]
[md5:16]
[bytes_written:u32 BE]
[total_size:u32 BE]
[ff ff ff ff]                              — sentinel
[status:1]                                 — XOR(every body byte above)
```

`size_LE` counts every byte after the first 6 (i.e. `msg_len = size + 6`). The two `00 00` bytes that follow the 6-byte header look like they could be extra header pad, but they are part of the body and contribute to the XOR (as zeros they are no-ops).

**Sim impact.** The pre-2026-04-24 sim emitted a 4-byte `ff ff ff ff` trailer — 3 bytes longer than the real wheel — and set `size_LE = body_len` assuming an 8-byte header. Both errors compounded: PitHouse parsed size field, walked N bytes into the body, and its internal state machine's `next_message_offset` pointer landed 3 bytes past the real message end, at which point the sentinel scan / status XOR check failed and PitHouse sat on "resources syncing" waiting for a message it would never recognise. `build_file_transfer_response` in `sim/wheel_sim.py` now emits a 1-byte XOR status byte and sets `size = body_len + 2`.

### Session data chunk CRC — 4 bytes LE

**Verified 2026-04-24 (again).** Each session `7c:00` data chunk carries a **4-byte CRC32-LE** trailer over the net body. A previous revision of this section briefly claimed 3 bytes; that claim was an artifact of a buggy `extract_frames` helper that dropped the last 2 bytes of each frame (real CRC's last byte + frame checksum). When raw tshark output is inspected directly every chunk's last 4 bytes match `zlib.crc32(net)` LE exactly.

Full chunk wire layout: 6-byte `7c:00:sess:01:seq_lo:seq_hi` + 54-byte net data + 4-byte CRC32-LE = 64-byte payload = 69-byte frame (with `7e/N/group/device/cksum` framing, `N = 0x40`). The final chunk of a message is shorter; it still carries a 4-byte CRC over its (smaller) net data.

Sim chunking (`chunk_session_payload`, `_chunk_catalog_message`) and all chunk-CRC-aware ingestion paths (`UploadTracker.feed`, `PitHouseUploadReassembler.add`) use 4-byte CRC. `chunk_session_payload` exposes a `crc_bytes` knob for future firmware variants but defaults to 4.

### Multi-round upload content (type=0x03) — zlib reassembly

> **NOTE 2026-04-24**: this section described an 8-byte sub-msg header. That interpretation worked on session 0x07 captures by accident (chunk-stride misalignment landed on valid LZ77 boundaries) but failed on larger uploads / session 0x09. The real header is 6 bytes; continuations have a per-chunk variable header before the deflate continuation. See [`6-byte-submsg-header.md`](6-byte-submsg-header.md) and [`per-chunk-trailer.md`](per-chunk-trailer.md) (continuation chunks) for the corrected layout. The legacy 8B-header parser is kept as a fallback in `_parse_upload` for older firmware, but new firmware should hit the 6B path.

Large dashboards (≥ ~10KB compressed) are split across many type=0x03 sub-msgs. Original (legacy 8B-header) interpretation:

```
[03] [size_LE:u32] [00 00 00]                  — 8B sub-msg header (LEGACY — actually 6B; trailing 2 zeros are body, see below)
[LOCAL TLV]                                    — 0x8c 0x00 + UTF-16LE Windows temp path + 00 00
[REMOTE TLV]                                   — 0x70 0x00 + UTF-16LE /_moza_filetransfer_md5_<hex> + 00 00
[0x10] [md5:16]
[reserved:4]
[token:4]
[compressed_header:8]                          — uncomp_sz BE + comp_sz LE (mixed endian)
[zlib_or_raw_deflate_chunk]                    — `78 9c` magic only on FIRST sub-msg; subsequent sub-msgs carry raw deflate continuation at the same byte offset
```

Observed `size_LE = 0x1120 = 4384` for every sub-msg in a 506KB upload on the user's 2026-04 PitHouse; under the 8B reading the zlib data starts 769 bytes into each sub-msg. Under the corrected 6B reading the same data starts at body[1267] in chunk0 and body[290] in continuations.

**Reassembly algorithm — legacy fallback** (`_parse_upload` in `sim/wheel_sim.py`):

1. Anchor on the LAST type=0x02 metadata marker in the session buffer (PitHouse may retry → stale type=0x02 / type=0x03 blocks earlier in the buffer must be skipped).
2. From the anchor, enumerate all following type=0x03 sub-msgs where `size_LE` is in the plausible 1000–10000 range.
3. In the first sub-msg, find the `78 9c` zlib magic → derive `zoff_in_msg`.
4. For each sub-msg (first and continuations), slice `buf[off + zoff_in_msg : off + 8 + size_LE]` and concatenate. This strips the (mistakenly-interpreted) 8B sub-msg header + path TLVs + md5 + tokens + compressed_header from every continuation.
5. Feed the concatenated deflate stream through `zlib.decompressobj()`. If `d.eof`, the upload is complete; else it was truncated but still yields partial bytes which sim writes to its virtual FS (better to store partial mzdash than nothing).

For new firmware always prefer the 6B-header path (`_parse_upload_6b`); see below.

**`_scan_file_transfer_paths` anchoring.** The metadata-field extractor (md5, total_size, local path) also anchors on the LAST type=0x02 boundary for the same reason — otherwise on retries the sim ends up building reply bodies that concatenate paths from the stale attempt with paths from the fresh attempt, inflating body length and shifting the size field.
