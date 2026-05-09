### Multi-attempt upload interleaving in the buffer

PitHouse retransmits cause chunks from different upload attempts to coexist
in the session buffer. Each attempt has its own chunk0 (counter=0).
Continuation chunks from one attempt do **not** cleanly continue another
attempt's deflate stream — they belong to a different zlib instance and
must not be glued across attempt boundaries.

### Why this happens

`type=0x03` sub-msgs carry the file content. PitHouse will resend earlier
sub-msgs (with the same TLV path block, same MD5, same flags) when the
device's `bytes_written` ack lags or arrives out of order. The retransmits
are byte-identical at the prefix (offset 0..280) — only the per-chunk
counter at offset 281–283 differentiates them. Consecutive type=0x03 chunks
in the buffer are therefore *not* guaranteed to belong to the same attempt
or to extend the same deflate stream.

### Sim parsing strategy

`_parse_upload_6b` in [`sim/wheel_sim.py`](../../../sim/wheel_sim.py)
implements a greedy walk that filters across attempt boundaries:

| Step | Operation | Purpose |
|------|-----------|---------|
| 1 | Walk buffer with the 6B-header validator (`type ∈ {01, 02, 03, 11}`, `pad == 00 00 00`, stride matches `6 + size_LE`) | Skip frames that aren't valid sub-msgs (e.g. interleaved unrelated chunks) |
| 2 | Find chunk0 = first `type=0x03` where `body[281:284] == 0` AND `78 9c` magic present | Lock onto a deflate-stream start |
| 3 | Initialize `zlib.decompressobj()` with `body0[zoff:]` | Begin streaming decode |
| 4 | For each remaining `type=0x03`: scan continuation offsets in `[280, 1500)` and pick the offset that produces the **longest clean decompression extension** | Reject continuation candidates that would corrupt the stream |
| 5 | Repeat step 4 until no chunk fits cleanly OR `decompressobj.eof` | Bounded by deflate EOF |

### Per-chunk header layout

Continuation chunks carry the same shared TLV envelope as chunk0 in the
first 280 bytes, but the body region after that is per-chunk:

| Offset | Size | Meaning |
|--------|------|---------|
| 0–280 | 281 | Shared TLV envelope (LOCAL `0x8C` path + REMOTE `0x70` path + flags + MD5 + token) — identical in every chunk of one attempt |
| 281–283 | 3 | LE counter — `0` on chunk0, monotonic on continuation chunks (PitHouse's notion of stream-resume position) |
| 284–289 | 6 | Constant `03 92 16 00 00 0f` |
| 290 | varies | If counter==0: dest-path TLV (UTF-16BE) + 8-byte compressed_header, then `78 9c` zlib magic at body[~1267]. If counter>0: raw deflate continuation starts at body[290] directly |

See [`per-chunk-trailer.md`](per-chunk-trailer.md) for the full continuation-
chunk byte map.

### Why "longest extension" wins

When two attempts interleave, picking the wrong continuation offset will
yield a shorter decompression extension (often zero — `decompressobj`
raises) before failing. The right offset always extends further because
it's the actual continuation. Greedy "longest-extension" rule converges
without state from earlier validations.

### Verified outcomes

- 62 KB session 0x09 buffer with 14 `type=0x03` chunks (mostly
  retransmits) → 82 KB decoded mzdash JSON.
- Decoded JSON root keys: `name='JDM Gauge Style 02'`, `version='1.1.1'`,
  `type='Window.qml'` — confirms reassembly preserved structure.

### Edge case: partial uploads

When PitHouse aborts mid-flight (window-close, profile switch), the
greedy walk stops at the last chunk that extends the stream. The
resulting partial mzdash is still written to the virtual FS — better to
keep partial structure than nothing.
