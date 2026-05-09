### Path B — session 0x04 device-initiated sub-msg 1/2 (observed in `dash-upload.ndjson`)

> **2025-11 firmware.** Wheels: VGS, CS. Captures: `latestcaps/automobilista2-*.pcapng`, `dash-upload.ndjson`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

Device initiates with type=0x81 channel open. Host then sends two sub-messages.

**Sub-message 1 — path registration (no file content):**
```
header(8)
  TLV paths (0x8C=local, 0x84=remote)
  MD5_len(1=0x10) + MD5(16)
  reserved(4=0x00000000)
  token(4)
  sentinel(4=0xFFFFFFFF)
```

**Sub-message 2 — file content push:**
```
header(8)
  TLV paths (0x8C=local, 0x84=remote)
  MD5_len(1=0x10) + MD5(16)
  reserved(4)
  token(4) + token(4)
  file_count(4)
  dest_path_byte_len(4)
  dest_path(UTF-16BE, null-terminated)
  compressed_header + zlib_stream
```

**8-byte transfer header:**

| Byte | Host→dev | Dev→host | Meaning |
|------|----------|----------|---------|
| 0 | `0x02` | `0x01` | Sender role (0x02=host, 0x01=device) |
| 1 | `0x40` (64) | `0x38` (56) | Max chunk payload size |
| 2 | `0x01` | `0x01` | Transfer type (0x01=file transfer) |
| 3–7 | zeros | zeros | Reserved |

**TLV path markers:**

| Marker | Meaning |
|--------|---------|
| `0x8C` | Local path (host-side temp file) |
| `0x84` | Remote path (device-side staging or target) |

Each entry: `marker(1) + 0x00(1) + UTF-16LE_path(null-terminated)`. Scan to null terminator for length.

Host paths: `C:/Users/.../AppData/Local/Temp/_moza_filetransfer_tmp_{timestamp}`
Device staging: `/home/root/_moza_filetransfer_md5_{md5hex}`
Device target: `/home/moza/resource/dashes/{name}/{name}.mzdash`

Note: TLV paths use UTF-16LE, but destination path in sub-message 2 uses **UTF-16BE**.

End-to-end file integrity uses **MD5** (transmitted alongside paths). On-device staging file named after MD5 hash.

**Session 4 sequence diagram:**

```
Device                                     Host
  │ ──── type=0x81 (channel open) ────────→  │  seq=0x0004
  │ ←─── fc:00 ACK ──────────────────────    │
  │ ←─── Sub-msg 1: path registration ───    │  7 chunks
  │ ──── fc:00 ACKs ─────────────────────→   │
  │ ──── Sub-msg 1 response (file ack) ───→  │  6 chunks
  │ ←─── Sub-msg 2: file content push ───    │  32 chunks
  │ ──── fc:00 ACKs ─────────────────────→   │
  │ ──── Sub-msg 2 response (file ack) ───→  │  6 chunks
  │ ←─── type=0x00 end marker ───────────    │
  │ ──── type=0x00 end marker ───────────→   │
```

**Sub-msg 1 / sub-msg 2 response format (device → host, ~318B, 6 chunks):**

Verified against `usb-capture/09-04-26/dash-upload.pcapng` (2026-04 firmware, CSP, 1355-byte mzdash upload). Both sub-msg responses share 8-byte-header + TLV-paths + trailing-metadata structure. Only `role` byte, `bytes_written` field, and trailing status byte differ.

| Offset | Size | Field | Value (sub-msg 1) | Value (sub-msg 2) |
|--------|------|-------|-------------------|-------------------|
| 0 | 1 | `role` | `0x01` | `0x11` |
| 1 | 1 | `max_chunk` | `0x38` (56) | `0x38` (56) |
| 2 | 1 | `ttype` | `0x01` (file transfer) | `0x01` |
| 3–7 | 5 | reserved | zeros | zeros |
| 8 | 1 | TLV marker | `0x84` (remote) | `0x84` |
| 9 | 1 | TLV separator | `0x00` | `0x00` |
| 10…R-2 | N | remote path | UTF-16LE NUL-term: `/home/root/_moza_filetransfer_md5_{md5hex}` | same |
| R | 1 | TLV marker | `0x8C` (local) | `0x8C` |
| R+1 | 1 | TLV separator | `0x00` | `0x00` |
| R+2…L-2 | M | local path | UTF-16LE NUL-term: host temp path | same |
| L | 1 | metadata flag | `0x10` | `0x10` |
| L+1 | 16 | MD5 | MD5 of received content | same |
| L+17 | 4 | `bytes_written` (BE u32) | `0x00000000` | `0x0000054B` (= total) |
| L+21 | 4 | `total_size` (BE u32) | `0x0000054B` (= uncompressed mzdash size) | same |
| L+25 | 4 | marker | `0xFFFFFFFF` | `0xFFFFFFFF` |
| L+29 | 1 | trailer / status | `0x6B` (in-progress) | `0x25` (complete) |

Interpretation:
- **Sub-msg 1 response** = "ack path registration; expecting `total_size` bytes, received 0." Host uses to confirm wheel ready, proceeds with sub-msg 2.
- **Sub-msg 2 response** = "ack file content; received all `total_size` bytes, MD5 matches." Host uses to confirm upload landed before sending type=0x00 end marker.
- `bytes_written` = `total_size` on sub-msg 2 = how wheel confirms whole file arrived.
- MD5 in metadata tail matches `{md5hex}` embedded in remote-path filename — content hash computed by wheel over decompressed mzdash after receipt.
- Trailer byte `0x6B` vs `0x25`: not fully decoded. Stable across repeated uploads of same file — probably status code (in-progress / complete) rather than CRC.

Both response structures chunked via standard `7c:00 type=0x01` SerialStream data chunks on session 0x04 with per-chunk CRC-32 trailers. Sim / reference implementation builds full ~318-byte message once, then pushes through `ChunkMessage(msg, session=0x04, seq)` for 6 wire chunks.

**2025-11 firmware note:** `automobilista2-wheel-connect-dash-change.pcapng` shows 2025-11 firmware's initial filesystem push on session 0x04 uses a DIFFERENT structure: subtype tag `0x0a`, 53-byte prefix (tag + LE size + BE path-length + UTF-16LE `/home/root` + `ff*8 00` padding + 14-byte unknown metadata) wrapping a zlib directory listing. Full byte-level layout documented in [`session-04-root-dir.md`](session-04-root-dir.md). That burst is NOT an upload response — it's a root directory listing. Under 2025-11, confirmation of fresh upload arrives as post-upload dir-listing refresh on session 0x04 (and updated configJson state blob on session 0x09), not as sub-msg 1/2 response with 2026-04 format. When implementing wheel sim: emit both paths based on `ttype`: `0x01` for per-sub-msg acks (2026-04 style), secondary root-listing refresh on END for 2025-11 parity.

### 2026-04 PitHouse omits the remote dashboard path

In the live 2026-04 upload payload (session 0x07, captured to `sim/logs/parse_upload_sess07_buf.bin`), the host TLV stream contains **no** `/home/root/resource/dashes/...` UTF-16LE string at all. The only path-shaped data is the PitHouse Windows stage path:

```
C:/Users/<user>/AppData/Local/MOZA Pit House/_dashes/<hash>/dashes/<NAME>/<NAME>.mzdash
```

with `/` as separator. The `<NAME>` segment is the user-visible dashboard name. Sim's `extract_mzdash_path` falls back to this regex when no `/home/root/...` path is present, so the upload lands at `/home/root/resource/dashes/<NAME>/<NAME>.mzdash` in the virtual FS instead of the placeholder `uploaded-dashboard`. The decoded mzdash JSON has a top-level `name` field with the same dashboard name (verified once full decode succeeded — see [`6-byte-submsg-header.md`](6-byte-submsg-header.md)), so either source agrees.
