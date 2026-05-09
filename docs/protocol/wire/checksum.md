### Checksum

`checksum = (0x0D + sum of all preceding bytes including 0x7E) % 256`

Magic value 13 (`0x0D`) incorporates USB endpoint (`0x02`), transfer type (`0x03` for URB_BULK), length constant (`0x08`). Changing it causes devices to not respond — likely firmware quirk.

### Checksum / body byte escape (0x7E byte stuffing)

When a computed checksum equals `0x7E`, sender **doubles it on wire** — transmits `0x7E 0x7E` instead of single `0x7E`. Receiver must consume extra byte after reading a frame whose checksum is `0x7E`. Without this, escape byte misinterpreted as start of new frame, desyncing subsequent parsing.

Applies to **both directions**. Confirmed from Wireshark USB captures (2026-04-18):

```
Host → device:  7e 06 3f 17 1a 01 3d 3f 00 00 7e 7e
                └── frame (cksum=0x7e) ──────────┘ └─ escape byte

Three 0x7E in a row (escaped checksum + next frame start):
Device → host:  7e 07 8e 21 00 00 0b 00 00 00 32 7e 7e 7e 07 8e 91 ...
                └── frame 1 (cksum=0x7e) ────────┘  │  └── frame 2 ─
                                              escape ┘
```

Three-`7E` case: first is checksum, second is escape, third is next frame start.

**Buffer parsing:** When extracting frames from concatenated USB bulk data, parser must skip escape byte between frames. Byte-at-a-time serial readers must consume one extra byte after frame with checksum `0x7E`. Failure causes escape `0x7E` read as frame start, next byte consumed as length field — typically a large value (e.g. `N=0x7E`=126) overshooting buffer, silently dropping subsequent frames.

**Scope:** Group IDs (0x07–0x64), device IDs (0x12–0x1E), and response transforms (group | 0x80, nibble-swapped device) never equal `0x7E`. However, **payload bytes CAN be `0x7E`** — observed in zlib-compressed session data (dashboard uploads) and device catalog frames. Host escapes every `0x7E` in body on wire by doubling. Frame boundary always 1 or 3 bytes of `0x7E` (single start, or escaped checksum + next start), never 2 — so `0x7E 0x7E` mid-frame is always escaped body/checksum byte, not boundary.

**Checksum computed on wire bytes (after escaping).** Host computes `(0x0D + sum)` over escaped representation. Each `0x7E` in decoded body (positions 2 through end-1) adds extra `0x7E` to wire-level sum. Receivers: `verify(frame)` adds `frame[2:-1].count(0x7E) * 0x7E` to computed checksum. `build_frame()` does same when computing outgoing checksum.

**Plugin impl note (2026-04-22):** the SimHub plugin previously used a raw-sum `CalculateChecksum()` that omitted the escape-count term, causing ~20% of zlib-bearing session chunks (configJson state, dashboard uploads) to fail verify and be silently dropped when their compressed payloads contained `0x7E` bytes. Fixed by routing all production send/verify paths through `MozaProtocol.CalculateWireChecksum()` which adds `count(0x7E in body positions 2..len-1) × 0x7E` to the sum.

Reference: [boxflat PR #131](https://github.com/Lawstorant/boxflat/pull/131).
