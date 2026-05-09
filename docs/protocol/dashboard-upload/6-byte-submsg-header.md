### 6-byte sub-msg header (new firmware, 2026-04+)

Every file-transfer sub-msg on the upload session (`0x04`–`0x07` depending
on firmware) starts with a fixed 6-byte header followed by the body. The
header type byte selects sub-msg semantics:

> **2026-04+ firmware.** Capture: `latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng`.
> Older firmware uses an 8-byte header (legacy parser fallback) — see
> [`../FIRMWARE.md`](../FIRMWARE.md).

### Header layout

```
[type:1] [size_LE:u16] [pad:3]
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | type | `0x01` ready/progress ack (dev → host); `0x02` metadata sub-msg (host → dev); `0x03` content sub-msg (host → dev); `0x08` dir-listing probe (host → dev); `0x0a` dir-listing reply (dev → host); `0x11` complete ack (dev → host) |
| 1 | 2 | size (LE u16) | Body length **excluding** the 6-byte header (i.e. message stride is `6 + size`) |
| 3 | 3 | pad | Always `00 00 00` |

### Stride

Consecutive sub-msgs in the buffer pack tightly: next sub-msg starts at
`current_offset + 6 + size`. Validating stride is the cheapest way to
distinguish real headers from accidental `type`-byte matches in body data.

### Why not 8 bytes

Earlier docs anchored on an 8-byte header (`[type:1][size:4][pad:3]`),
treating the 5 trailing zeros after the size as `pad`. That parse worked
on session 0x07 captures by accident — the 2 stray bytes (a misaligned
chunk-stride offset) happened to land on valid LZ77 token boundaries
inside the deflate stream of small dashboards. On larger uploads
(session 0x09, 500 KB+ dashboards) those same 2 bytes fell on invalid
block-type bits and the stream errored mid-decode.

The real header is **6 bytes**. The "5-zero pattern" earlier docs
relied on is actually `pad(3) + body[0:2]` where `body[0..1]` is
typically `00 00` for `type=0x02` metadata sub-msgs (because the LOCAL
path TLV starts `8c 00 …` and `body[0]` = `0x8c`, `body[1]` = `0x00` —
the `8c` byte breaks the regex on closer inspection). Use the 6-byte
header and validate via stride, not via tail-zero regex.

### Worked example

Bytes from a `type=0x02` metadata sub-msg with 316-byte body:

```
02              type
3C 01           size_LE = 316
00 00 00        pad
[316 bytes]     body
```

Total wire length: 6 + 316 = 322 bytes. Next sub-msg starts at offset 322.

### Fallback for older firmware

The legacy 8-byte parser is retained in `_parse_upload` (sim) /
`UploadTracker.feed` (plugin) as a fallback for older firmware where the
stride doesn't match the 6-byte rule. Both implementations try the 6-byte
path first; failure (stride mismatch on the second sub-msg) drops to the
legacy parser.
