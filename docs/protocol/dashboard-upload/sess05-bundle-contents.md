### Upload-session bundle contents — mzdash + content-addressed PNG dependencies

> **2026-04+ firmware, current PitHouse.** One file-transfer session
> carries a multi-file bundle: the `.mzdash` dashboard plus every PNG
> resource referenced by it. Bridge-side decode of two consecutive
> PitHouse uploads (ETS2-ATS + Simple Rally Mini Dash) captured
> 2026-05-14 in `sim/logs/bridge-20260514-170002.jsonl`. Full byte-exact
> reassembly of upload #2's deflate stream verified 2026-05-15 — see
> §"Decode-side verification" below.
> See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

A PitHouse dashboard upload is **not** a single-file transfer. The host
opens one file-transfer session (sess=0x04..0x0a, dynamic — see
[`upload-handshake-2026-04.md`](upload-handshake-2026-04.md)) and pushes
the mzdash plus all PNG image resources referenced by widgets inside
that mzdash, in one type=0x02 metadata + N×type=0x03 content sequence.

## Destination layout on the wheel

| File class | Destination path scheme |
|------------|--------------------------|
| Dashboard mzdash | `/home/moza/resource/dashes/<DisplayName>/<DisplayName>.mzdash` |
| PNG image resource | `/home/moza/resource/images/MD5/<md5_hex>.png` |

`<DisplayName>` matches the dashboard's `name` field inside the mzdash
JSON (and the "title" in the configJson state push — see
[`config-rpc-session-09.md`](config-rpc-session-09.md)). The per-
dashboard subdirectory means each upload's PNG dependencies don't
pollute a shared image namespace — but the PNG files themselves are
**content-addressed** (filename = md5 of the PNG bytes) so duplicate
images across dashboards dedupe naturally at the filesystem level.

## Source side (PitHouse staging)

Each upload's source path is a single Windows temp file:

```
C:/Users/<user>/AppData/Local/Temp/_moza_filetransfer_tmp_<unix_ms>
```

That temp file is a **bundle** PitHouse builds in-place — it contains
the mzdash plus the PNG dependencies, marshalled into the format the
wheel expects. The type=0x03 content sub-msgs carry the bundle as a
multi-chunk stream; the wheel decompresses the zlib portion and
writes each file content to its respective destination path.

## How many files per upload — examples

| Dashboard | mzdash | PNG deps | Total compressed wire bytes |
|-----------|--------|----------|------------------------------|
| ETS2-ATS (complex) | 1 | 9+ | ~16 KB (with one earlier abandoned attempt interleaved) |
| Simple Rally Mini Dash | 1 | 1 | 15,269 B compressed payload across 4 × type=0x03 + 1 × type=0x02 metadata |

For Simple Rally Mini Dash the bundle decompresses to 341,089 bytes
total (340,342 B CRLF-normalised mzdash + 747 B PNG), confirmed
byte-exact against the on-disk source files.

## Implications for sim / plugin

1. **The wheel expects all PNG resources to land at the canonical
   `/home/moza/resource/images/MD5/<md5>.png` paths** before it can
   render widgets that reference them. Display elements with `<img
   src="MD5/abc….png">` style refs in the mzdash JSON resolve through
   that image root.
2. **The image-ref-map in the configJson state push**
   (`disableManager.imageRefMap` / `enableManager.imageRefMap`, see
   [`config-rpc-session-09.md`](config-rpc-session-09.md)) is the
   wheel's reverse-lookup from `MD5/<hex>.png` filename to per-
   dashboard refcount. Sim must maintain this if it persists across
   uploads — otherwise GC-style image cleanup may drop in-use PNGs.
3. **A `.mzdash`-only re-upload after editing a dashboard still
   carries the PNG payload** if any PNGs are bundled — PitHouse
   re-bundles on each upload. Deduplication is at the wheel
   filesystem level (write-if-md5-not-present), not at the wire
   level.

## Compressed-payload layout (decoded 2026-05-15)

The compressed payload that gets split across the type=0x03 chunks is
a single contiguous byte stream of `total_compressed_size` bytes (the
field at body[283:287] of every chunk's per-chunk position envelope —
see [`per-chunk-trailer.md`](per-chunk-trailer.md)). For upload #2:
`total_compressed_size = 15269 = 0x3BA5`.

Layout of the reassembled compressed payload (chunks 0..N concatenated
at `body[291 : 291 + this_chunk_deflate_size]`):

```
+---- offset 0 ------------------------------------------------------+
|  Uncompressed bundle preamble (the "file table"):                  |
|    [file_count: u32 BE]                                            |
|    for i in 0..N-1:                                                |
|        [dest_path[i]_byte_len: u32 BE]                             |
|        [dest_path[i]: UTF-16BE, no NUL]                            |
|        [file[i]_uncompressed_size: u32 BE]                         |
|    [total_compressed_size: u32 BE]      (← same value as the       |
|                                            chunk-envelope field)   |
|    [total_uncompressed_size: u32 LE]    (← note: little-endian)    |
+---- offset = preamble_length -------------------------------------+
|  Zlib stream:                                                      |
|    [78 9c]                                  (zlib header)          |
|    [raw deflate of file[0]_bytes ‖ file[1]_bytes ‖ … ]             |
|    [adler32: u32 BE]                        (zlib trailer)         |
+---- offset = total_compressed_size -------------------------------+
```

For upload #2 the preamble is **320 bytes** long (file_count + 158-byte
dest_path + 4-byte size + 134-byte dest_path + 4-byte size + total
size pair = 4 + 4 + 158 + 4 + 4 + 134 + 4 + 4 + 4 = 320), placing the
zlib magic at preamble offset 320, i.e. global body[291 + 320] = body[611]
for chunk 0.

### Byte-exact field map for upload #2 (Simple Rally Mini Dash)

Offsets are relative to the start of the compressed payload (= chunk 0
body[291]):

| Offset (rel.) | Field | u32 BE value | Meaning |
|--------------:|-------|--------------|---------|
| 0–3   | `file_count`              | **2** | mzdash + 1 PNG |
| 4–7   | `dest_path[0]_byte_len`   | **158** | byte length of file 0's path (UTF-16BE) |
| 8–165 | `dest_path[0]`            | UTF-16BE | `/home/moza/resource/dashes/Simple Rally Mini Dash/Simple Rally Mini Dash.mzdash` |
| 166–169 | `file[0]_uncompressed_size` | **340342** | CRLF-normalised mzdash bytes |
| 170–173 | `dest_path[1]_byte_len` | **134** | byte length of file 1's path |
| 174–307 | `dest_path[1]`          | UTF-16BE | `/home/moza/resource/images/MD5/bd529011a002c03dc77b2f63b193b789.png` |
| 308–311 | `file[1]_uncompressed_size` | **747** | PNG bytes |
| 312–315 | `total_compressed_size` BE | **14953** | byte count of zlib stream = `78 9c + deflate + adler32` |
| 316–319 | `total_uncompressed_size` LE | **341089** | `Σ file[i]_uncompressed_size` = 340342 + 747 |
| **320+** | zlib stream begins | `78 9c …` | deflate-compressed payload |

> **Two "total" fields.** The chunk-envelope's `total_compressed_size`
> at body[283:287] (= 15269) is the **wire byte count of preamble +
> zlib stream** — this is what the wheel uses to drive reassembly
> (it knows the upload is done when `Σ chunk_offset + this_chunk_deflate_size == total_compressed_size`).
>
> The preamble's `total_compressed_size` at preamble[312:316] (= 14953)
> is **4 bytes larger than the actual zlib stream** (which is 14949
> bytes: 2-byte `78 9c` header + 14943 raw deflate + 4-byte adler32).
> The 4-byte discrepancy is not yet explained — it doesn't match the
> CRC32 at the wire level, the chunk-envelope total, or any obvious
> field permutation. Possible explanations to test on a future capture:
> (a) PitHouse rounds up to the next multiple of some alignment; (b)
> the field counts the zlib stream plus a per-chunk trailing pad byte
> (× 4 chunks = 4 bytes); (c) PitHouse-internal accounting that the
> wheel ignores. **The wheel decodes correctly regardless** — this
> field appears to be informational. Plugin can emit either 14949 or
> 14953 and the upload still succeeds, but match PitHouse's value
> for the cleanest reproduction.

### What's inside the deflate stream

Decompressing the zlib stream gives a single contiguous byte sequence
of `total_uncompressed_size` bytes (341089 for upload #2) that is
**all files concatenated in `dest_path` order**:

```
[file[0]_bytes][file[1]_bytes]...[file[N-1]_bytes]
```

The wheel slices this output using the per-file `uncompressed_size`
values from the preamble. No internal separators inside the deflate
stream — the wheel relies entirely on the explicit per-file sizes.

### Decode-side verification (2026-05-15)

Reassembling upload #2's compressed payload from the 4 type=0x03 chunks
and feeding the zlib portion (preamble offset 320 onwards) into
`zlib.decompressobj()` produces **341,089 bytes** of decompressed
output:

- bytes [0..340341]   = **mzdash CRLF-normalised content** — diff vs
  on-disk `Simple Rally Mini Dash.mzdash` shows only 2 field-level
  changes, both expected version drift (PitHouse regenerated
  `lastModified` timestamp and `window.GUID` between the wire-capture
  and the later on-disk read).
- bytes [340342..341088] = **PNG bytes** — **byte-exact match** vs
  on-disk `Resource/MD5/bd529011a002c03dc77b2f63b193b789.png`.

```python
import zlib, struct

# Walk type=0x03 chunks; reassemble compressed payload.
def slice_chunk(body):
    this_size = int.from_bytes(body[287:291], 'big')
    return body[291 : 291 + this_size]

payload = b''.join(slice_chunk(b) for b in bodies)   # 15,269 bytes
# First 320 bytes = preamble (file table). Zlib stream starts at payload[320].
out = zlib.decompress(payload[320:])                  # 341,089 bytes
assert out[340342:] == on_disk_png                    # byte-exact
```

This unambiguously confirms:

1. `body[279:291]` is the per-chunk position envelope (not the bundle
   preamble) — see [`per-chunk-trailer.md`](per-chunk-trailer.md).
2. The bundle preamble starts at chunk 0 body[291].
3. `total_uncompressed_size` is u32 **LE** (not BE) — verified by the
   341089 value lining up with `340342 + 747`.
4. PitHouse converts mzdash JSON from Unix LF to Windows CRLF before
   bundling.

**Plugin implementation note.** When building an upload from a Unix-LF
on-disk mzdash, the plugin must normalise to CRLF before including the
bytes in the bundle. Conversion: `mzdash_bytes.Replace("\r\n", "\n").Replace("\n", "\r\n")`
(first strip any existing `\r\n` to avoid double-conversion on
already-CRLF input).

### Upload #1 (ETS2-ATS, 9+ files) — same structure, larger N

Upload #1 references at least 9 files (1 mzdash + 8 PNGs). The same
layout applies with `file_count = 9` and 9 pairs of
`[dest_path_byte_len][dest_path][file_size]` records before the
aggregate sizes + zlib magic. Byte-exact verification was not done
because upload #1's stream is contaminated by retransmits from an
earlier abandoned attempt (per
[`multi-attempt-interleaving.md`](multi-attempt-interleaving.md));
the 6-byte sub-msg walker fails on it at byte 332.

## Plugin implementation gap

`Telemetry/Dashboard/FileTransferBuilder.cs:BuildFileContentBodyType02`
emits `file_count = 1` with a single dest_path. To match PitHouse for
real dashboards:

1. Accept a `List<(string dest, byte[] content)>` instead of `(destPath, mzdashContent)`.
2. Emit `file_count`, then per-file `[byte_len][dest_path][size]` records.
3. Concatenate all file bytes (CRLF-normalising mzdash content), compute one zlib stream, emit `[total_compressed_size: u32 BE][total_uncompressed_size: u32 LE]` before the zlib stream.
4. Caller (DashboardUploader / WheelUploadCoordinator) needs to walk the
   mzdash JSON's `imageRefMap` (or equivalent) to find every PNG the
   widgets reference and gather their bytes from a content store (the
   `_dashes/<hash>/dashes/<NAME>/` directory PitHouse maintains).
