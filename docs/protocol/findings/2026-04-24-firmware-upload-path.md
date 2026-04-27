## Findings from 2026-04-24 session (firmware upload path on new builds)

Investigation was driven by PitHouse refusing to commit dashboard upload bytes to the wire against the sim — UI showed "resources syncing" indefinitely after user clicked upload. Traffic inspection revealed multiple protocol assumptions wrong for the current (2026-04) PitHouse build.

> **Canonical topical homes for facts in this journal:**
>
> | H3 section | Now also documented at |
> |------------|------------------------|
> | File upload session is NOT 0x06 — varies per firmware | [`../dashboard-upload/upload-handshake-2026-04.md`](../dashboard-upload/upload-handshake-2026-04.md) |
> | Device-side `7c:23` page-activate frames vary per wheel | [`../dashboard-upload/upload-handshake-2026-04.md`](../dashboard-upload/upload-handshake-2026-04.md) |
> | `7c:23` is a device-initiated session-open request | [`../sessions/lifecycle.md`](../sessions/lifecycle.md) |
> | Session 0x04 directory-listing probe/reply format | [`../dashboard-upload/session-04-root-dir.md`](../dashboard-upload/session-04-root-dir.md) |
> | Dir-listing reply 176B tail not zlib | [`../dashboard-upload/session-04-root-dir.md`](../dashboard-upload/session-04-root-dir.md) |
> | File-transfer sub-message format — LOCAL marker varies | [`../dashboard-upload/path-b-session-04.md`](../dashboard-upload/path-b-session-04.md) |
> | empty `enableManager.dashboards` no longer blocks handshake | [`../dashboard-upload/config-rpc-session-09.md`](../dashboard-upload/config-rpc-session-09.md) |

### File upload session is NOT 0x06 — varies per firmware

Earlier docs + sim code hardcoded session `0x06` as the file-transfer session (from the 2025-11 pithouse-switch-list-delete-upload-reupload.pcapng capture). **2026-04 PitHouse opens session `0x05` instead and streams the upload there.** Sim must treat the session number as dynamic — discovered at runtime from the host's session-open request.

| Firmware | Upload session | Source |
|----------|----------------|--------|
| 2025-11 (latestcaps captures) | `0x06` | `pithouse-switch-list-delete-upload-reupload.pcapng` |
| 2026-04 (early observations) | `0x05` | Live sim capture 2026-04-24 |
| 2026-04 (later live runs) | `0x07`, `0x09` | Live sim runs 2026-04-24 — both observed in same firmware build, port chosen at runtime by host's `7c:23` request. Sim now accepts any session in 0x04..0x0a as a candidate file-transfer session. |

### Device-side `7c:23` page-activate frames vary per wheel

Wheel→host `7c:23` advertises the wheel's available dashboard pages and is emitted continuously at ~1 Hz before and after session open. PitHouse appears to gate dashboard detection on the wheel-specific frame layout — sending the wrong set leaves Dashboard Manager partially populated even when wheel + display identity match.

| Wheel | Variant payload (after `7c 23`) | Trailer | Pages | Source |
|-------|---------------------------------|---------|-------|--------|
| VGS | `32 80 03 00 01 00`, `3c 80 04 00 02 00`, `50 80 05 00 03 00` | `fe 01` | 3 | `connect-wheel-start-game.pcapng` |
| CSP | `3c 80 03 00 01 00`, `32 80 04 00 02 00` | `fe 01` | 2 | `pithouse-complete.txt` |
| KSP | `32 80 04 00 01 00`, `3c 80 05 00 02 00`, `50 80 06 00 03 00` | **`fc 03`** | 3 | `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng` t=26.86–28.00 |
| KS | (none — wheel has no dashboard) | — | — | passive probe of real KS |

KSP differs from VGS+CSP in **both** the trailer (`fc 03` vs `fe 01`) AND the page-byte starting offset (port begins at `0x04` vs `0x03`). Earlier sim builds reused the CSP frame set for KSP; PitHouse showed the wheel as KS Pro but never populated its dashboard manager. Fixed 2026-04-26: model dict's `_7c23_frames_name` now resolves to `_7C_23_FRAMES_KSPRO`.

### `7c:23` is a device-initiated session-open request, not just a page notification

`7C:23` was documented as "dashboard-activate notify" (one-way). It is actually a **host → wheel session-open request** for a specific protocol variant:

```
7c 23 46 80 [seq:u16 LE] [port:u16 LE] fe 01     (10B)
```

- `46 80` is constant across all captures
- `[port]` is the session number the host wants the wheel to open
- Wheel replies with device-initiated session open:
  `7c 00 [port] 81 [port:u16 LE] [port:u16 LE] fd 02`

Observed variants in the captures:

| Host payload | Upload session port | Observed |
|--------------|---------------------|----------|
| `7c 23 46 80 06 00 04 00 fe 01` | 4 | No session open followed (2025-11 captures) |
| `7c 23 46 80 08 00 06 00 fe 01` | 6 | 3× session-open in upload capture |
| `7c 23 46 80 0a 00 08 00 fe 01` | 8 | 1× variant, not session-open in that capture |
| `7c 23 46 80 07 00 05 00 fe 01` | 5 | Observed 2026-04 firmware, triggers session 0x05 open |

Byte 4 (`[seq_lo]`) appears to be a sequence counter; the low nibble of byte 5 (`[seq_hi]` / port) selects the session. Not every variant triggers a session open — something else gates which `7c:23` opens a session and which is purely a dashboard-page notification. Current sim heuristic: any `7c 23 46 80 ... xx 00 fe 01` where byte 6 is the port opens that session. Works for observed captures.

### Session 0x04 directory-listing probe/reply format

Right after session 0x04 opens, **host sends a type=0x08 probe** asking for `/home/root` directory contents. Wheel must reply with **type=0x0a** or PitHouse falls back to its cache and silently skips any pending uploads (even though UI shows wheel FS as empty).

Host probe layout (51B body, tagged `display_cfg` by sim until 2026-04-24):

```
offset 0..7    08 [size_LE:u32=0x2c=44] [00 00 00]    header
offset 8..9    14 00                                   path_len field (u16 LE = 20)
offset 10..28  2f 00 68 00 6f 00 6d 00 65 00 2f 00 72 00 6f 00 6f 00 74   19B UTF-16LE "/home/root" — NO trailing null despite path_len=20 (firmware quirk)
offset 29..36  ff ff ff ff ff ff ff ff                8B constant
offset 37..44  xx xx xx xx xx xx xx xx                8B echo_id (request identifier)
offset 45..48  ff ff ff ff                            trailer sentinel
```

Wheel reply (221B body observed across both latestcaps captures):

```
offset 0..7    0a [size_LE:u32=0xd5=213] [00 00 00]   header
offset 8..9    14 00                                   path_len
offset 10..28  [path]                                  19B UTF-16LE "/home/root"
offset 29..36  ff ff ff ff ff ff ff ff                8B constant
offset 37..44  [echo_id]                               ECHOED FROM PROBE
offset 45..47  00 00 00                                3B zero
offset 48..52  a9 88 01 00 00                          5B constant (function unknown; maybe magic)
offset 53..220 [176B opaque tail]                      format undecoded (see below)
```

**Sim implementation**: `build_dir_listing_reply(echo_id)` at `sim/wheel_sim.py` ~line 850. Replays the captured 176B tail byte-exact, substituting only echo_id at runtime.

### Dir-listing reply 176B tail — NOT decodable as zlib

The 176B payload after the 5B `a9 88 01 00 00` magic is NOT zlib-compressed despite the `78` byte at offset 53 suggesting a zlib CMF header. Investigation:

- Position 53: `78` (constant across captures)
- Position 54: varies per capture (`97` in automobilista2, `a5` in pithouse-switch) — nonce/salt candidate
- Position 55 onward: identical across two captures with same wheel state (11 factory dashboards)
- No valid zlib header at any offset 45..70 with wbits `-15`, `15`, or `31`
- Not adler32, not CRC32 of known inputs, not derivable from md5/sizes
- Entropy 6.94 bits/byte → high but not maximal (consistent with compressed or encrypted data)

Candidates: custom compression, RC4-style stream cipher keyed on echo_id, or a hash of firmware state. **Recommend firmware reverse-engineering to decode.** Replay works functionally (PitHouse accepts format) but cache-skip logic is not defeated by it.

### File-transfer sub-message format — LOCAL marker varies

Host upload (session 0x05/0x06 depending on firmware) sub-messages:

```
[type:1] [size_LE:u32] [00 00 00]                    8B header
[marker:u16] [UTF-16LE path] [00 00]                 first path TLV (Windows local temp)
[marker:u16] [UTF-16LE path] [00 00]                 second path TLV (remote or local again)
10                                                   flag byte
[md5:16]                                             md5 of upload file
[bytes_written:u32 BE]                               BIG ENDIAN u32 (NOT little-endian)
[total_size:u32 BE]                                  BIG ENDIAN u32
ff ff ff ff                                          sentinel
[trailer:4]                                          unknown 4B value (see below)
```

| Sub-msg type | Direction | Role |
|--------------|-----------|------|
| `0x02` | host → wheel | Upload metadata (path + md5 + size) |
| `0x03` | host → wheel | Upload content (includes zlib file body) |
| `0x08` | host → wheel | Directory-listing probe (session 0x04) |
| `0x00` | host → wheel | RPC call (session 0x04 / 0x0a, zlib-compressed JSON) |
| `0x01` | wheel → host | Ready-ack (response to `0x02`) |
| `0x11` | wheel → host | Content-complete ack (response to `0x03`) |
| `0x0a` | wheel → host | Dir-listing reply (response to `0x08`) |

**LOCAL path marker byte changed between firmware versions:**

| Firmware | LOCAL marker | REMOTE marker |
|----------|--------------|---------------|
| 2025-11 (latestcaps) | `0x8A 0x00` | `0x70 0x00` (in wheel reply) / second `0x8A` (in host type=0x02) |
| 2026-04 (current) | `0x8C 0x00` | `0x70 0x00` |

Sim code in `_scan_file_transfer_paths` accepts both `0x8A` and `0x8C` markers for compatibility.

### empty `enableManager.dashboards` no longer blocks handshake

Prior doc (circa 2026-04-22) warned that empty `enableManager.dashboards` in session 0x09 configJson state caused sessions_opened to stay 0 and tier_def_received to stay false. **No longer reproducible (2026-04-24).** Sim now reports `configJsonList: []` and `enableManager.dashboards: []` without regression — handshake completes, display_detected=true, tier_def_received=true. The older observation likely interacted with some other gate that has since been addressed (rootDirPath, hub/base identity, short-form probes). Leaving the factory fallback in place did not trigger uploads either; it only reinforced PitHouse's cache-skip when dashboards matched factory names.
