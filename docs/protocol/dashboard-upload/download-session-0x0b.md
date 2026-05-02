# Dashboard download — FT session file transfer (device → host)

**Date:** 2026-05-01 (updated)
**Captures:**
- `sim/logs/bridge-20260501-073603.jsonl` — PitHouse cold-start, 20 files, session 0x0B (~62KB request, ~896KB response)
- `sim/logs/bridge-20260501-115203.jsonl` — PitHouse cold-start, 18 files, session 0x04 (~28KB request, ~358KB response)
**Hardware:** CSP firmware (R5 base + W17 wheel) — PitHouse 1.2.6.17
**Status:** Full protocol decoded from both captures. Upload sub-msg-1 prerequisite identified.

## Summary

PitHouse downloads dashboard mzdash files + images from the wheel via the file-transfer session during cold-start. The session number is **dynamic** (0x04, 0x05, 0x0B depending on firmware/timing). The download requires a **prerequisite upload sub-msg-1 handshake** before the actual request.

## Full session sequence (from capture 2, session 0x04)

### Phase 1: Upload sub-msg-1 handshake (prerequisite)
1. Wheel device-inits session 0x04
2. PitHouse acks, sends upload sub-msg-1 path registration (1 chunk, 54 bytes)
3. Wheel responds with ~5 data chunks (239-byte echo)
4. Session closes
5. (Retries if needed — PitHouse sends same handshake on each session re-open)

### Phase 2: Download
1. Wheel re-opens session 0x04
2. PitHouse sends download request (525 chunks, ~28KB)
3. Wheel responds after ~54s with download data (6,659 chunks, ~358KB)

**Critical:** Without the upload sub-msg-1 handshake, the wheel acks all download request chunks but never responds with file data.

## Upload sub-msg-1 handshake format

```
08 2C 00 00 00 00 00 00   header: type=0x08, size=0x2C(44)
14 00                      path byte count LE16 = 20
2F 00 68 00 6F 00 6D 00   /hom
65 00 2F 00 72 00 6F 00   e/ro
6F 00 74                   ot
FF FF FF FF FF FF FF FF    sentinel (8 bytes)
03 18 C8                   token
10 00 00 00 00             MD5 length(16) + 4 zero pad
FF FF FF FF                sentinel
DE                         trailing byte
[4 bytes CRC]              chunk CRC trailer
```

Wheel responds with header `0A D5 00 00 00 00 00 00` + echo body containing the same path, sentinels, and zlib-compressed metadata.

## Download request format (7 sections)

### Section 1: Header (10 bytes)
```
00 00 [file_count+4:LE16] 00 00 00 [remote_path_bytes:BE16] 00
```
Examples:
- Capture 1 (20 files): `00 64 18 00 00 00 00 08 CE 00` (remote_bytes=2254)
- Capture 2 (18 files): `00 00 16 00 00 00 00 07 EE 00` (remote_bytes=2030)

Note: byte[1] differs between captures (0x64 vs 0x00). Meaning TBD.

### Section 2: Remote paths (UTF-16LE, comma-separated)
```
/home/moza/resource/dashes/{name}/{name}.mzdash,...,
/home/moza/resource/images/MD5/{hash}.png,...
```

### Section 3: Separator (4 bytes)
```
00 [sep_byte] FA 00
```
Where `sep_byte = file_count - 5`:
- 18 files → `00 0D FA 00`
- 20 files → `00 0F 7E 00` (capture 1 — second field byte differs)

### Section 4: Local dest paths (UTF-16LE, comma-separated)
```
C:/Users/{user}/AppData/Local/MOZA Pit House/_dashes/{device_id}/{name}/{name}.mzdash,...
```

### Section 5: Staging metadata
After local paths, before the manifest separator:
```
00 00                          null terminator (UTF-16LE null)
52 00                          remote staging path marker (0x52)
/tmp/_moza_filetransfer_tmp_{timestamp}  (UTF-16LE)
00 00                          null terminator
10                             MD5 length = 16
[16 bytes MD5]                 session MD5
00                             padding
```

### Section 6: Manifest separator (2 bytes)
```
00 0F
```

### Section 7: Transfer manifest
Sub-msg-1 style entries with `0x8C` (local path) and `0x52` (remote staging path) TLV pairs. 83 entries observed for 18 files. Each entry is 268 bytes with counter/token/sentinel fields.

## Response format (device → host)

### Sub-message 1 (metadata, type=0x02)
```
Header (8 bytes): 02 CC 00 00 00 00 00 00
Body: remote staging path TLVs (0x52 marker) + MD5 + metadata
```

### Content blocks (type=0x03, repeating)
Each block is 4360 bytes: 8-byte header + 172-byte path overhead + 4180-byte data portion.
```
Block header: 03 02 11 00 00 00 00 00
Path pair: 2 × (path_len:LE16 + path:UTF-16LE + null)
File data: 4180 bytes of zlib stream
```

### Decompression
Data portions form a single continuous zlib stream (header `78 9C`). Decompressed output = concatenated mzdash JSON files, split by tracking brace depth.

## Chunk CRC
Every chunk payload has a 4-byte CRC trailer appended by the transport layer. Strip before reassembly. `SessionDataReassembler.StripCrcTrailer()` handles this.
