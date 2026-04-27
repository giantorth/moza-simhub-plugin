### Path A — session 0x01 host-initiated FF-prefix upload (plugin implementation)

> **2026-04 legacy firmware.** Plugin still implements this path. Wheels: VGS. Capture: `09-04-26/dash-upload.pcapng`. See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix.

Confirmed by CRC-32 verification across VGS and CSP captures. Each sub-message:

```
[FF] [payload_size: u32 LE] [payload bytes]
[remaining_transfer_size: u32 LE]
[CRC32: u32 LE]                              ← covers ALL preceding bytes from FF through remaining_size
```

Three sub-messages sent:

| Field | Payload size | Content | Notes |
|-------|-------------|---------|-------|
| 0 | 16 bytes | Device tokens (session-specific, differs per wheel) | remaining = total size of fields 1+2 |
| 1 | 8 bytes | `9e 79 52 7d 07 00 00 00` — protocol constant | Identical between VGS and CSP. remaining=3. NOT a literal in PE binary (computed/serialized at runtime) |
| 2 | varies (VGS: 1350, CSP: 100) | Compressed mzdash content | 12B pre-header + zlib stream (last field, no remaining/CRC trailer) |

Each field except last followed by `remaining_transfer_size(4 LE) + CRC32(4)`. CRC covers all bytes from `FF` through `remaining_transfer_size`. Field 2 is last, no trailing remaining/CRC.

**Field 0 tokens** (16 bytes = two 8-byte LE values):
- Token 1 = `[random_u32 | 0x00000002]`
- Token 2 = `[unix_timestamp | 0x00000000]`

Confirmed from 8 sessions across VGS and CSP: token 2 always Unix timestamp of session start; token 1 high 32 bits always `0x00000002` (protocol version or request type); token 1 low 32 bits CSPRNG output (no deterministic relationship to timestamp — tested CRC-32, FNV-1a, DJB2, MurmurHash3, mt19937, 12 LCG variants, crypto hashes, all negative). Correlation IDs, not validated by wheel. Pithouse's `Sync_DashboardManager` uses `mcUid` (STM32 MCU hardware UID, via `MainMcuUidCommand`) as per-device routing key, but mcUid NOT encoded in upload tokens.

**Field 0 remaining semantics:** Field 0 remaining = total bytes of subsequent fields (field 1 block + field 2 block). Value `7200` observed corresponds to dashboards like "Formula Racing V1-Mission R" (7170B compressed + 38B framing = 7208). Verified by computing zlib-compressed sizes of all 47 Pithouse dashboards — formula `38 + compressed_size` matches captures. `0x1C20` NOT hardcoded constant. Field 1 remaining = `3` in all captures — NOT byte count (field 2 much larger). Semantics unknown; possibly field count or message type constant.

**Field 2 pre-zlib header** (12 bytes before `78 da` zlib magic):
```
[CRC32_or_hash: 4B] [08 00 00 00: constant] [uncompressed_size_BE: 4B]
```

Zlib-compressed content IS mzdash dashboard file — confirmed by partial decompression producing UTF-16LE channel names (`RpmAbsolute1`, etc.).

**Pithouse re-uploads dashboard on every connection** — confirmed in `moza-unplug-plug-wheel-to-base.pcapng` (VGS) and `CSP captures/pithouse-complete.txt` (CSP). Pithouse does not check what's already loaded — always pushes from internal state. May be prerequisite for telemetry.
