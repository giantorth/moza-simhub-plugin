## RPM and button LED color commands

Live LED color frames for the wheel's RPM strip and button matrix. Sent at
SimHub's update cadence (typically 60 Hz when telemetry is active). Two
companion commands per group: a **20-byte color chunk** and a **bitmask
selector** that picks which indices are lit. See
[`../devices/wheel-0x17.md` § Group `0x3F` Live Telemetry](../devices/wheel-0x17.md)
for the per-command rows.

### Frame layouts

**RPM color chunk** (`wheel-telemetry-rpm-colors`):

```
7E 14 3F 17 19 00 [20 bytes: 5 × (idx, R, G, B)] [checksum]
```

**RPM active-LED bitmask** (`wheel-send-rpm-telemetry`):

```
7E [N] 3F 17 1A 00 [bitmask LE: 2 or 4 bytes] [checksum]
```

**Button color chunk** (`wheel-telemetry-button-colors`):

```
7E 14 3F 17 19 01 [20 bytes: 5 × (idx, R, G, B)] [checksum]
```

**Button active-LED bitmask** (`wheel-send-buttons-telemetry`):

```
7E 03 3F 17 1A 01 [bitmask LE: 2 bytes] [checksum]
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `[N]` | Payload length (0x14 = 20 for color chunk; 4 for 16-LED bitmask, 6 for 17+) |
| 2 | `0x3F` | Wheel-config write group |
| 3 | `0x17` | Device wheel |
| 4 | `0x19` (color) / `0x1A` (bitmask) | Cmd ID byte 1 |
| 5 | `0x00` (RPM) / `0x01` (button) | Cmd ID byte 2 — selects RPM vs button group |
| 6.. | LED entries / bitmask | See per-command sections below |

### 20-byte color chunk format

Each chunk packs **5 LEDs × 4 bytes**:

| Offset within chunk | Field | Notes |
|---------------------|-------|-------|
| `0` | LED index | Physical LED position (`0xFF` = unused padding) |
| `1` | R | Red 0..255 |
| `2` | G | Green 0..255 |
| `3` | B | Blue 0..255 |
| `4..7` | Next LED | …repeats five times |

Chunks per group:

| Group | LED count | Chunks needed |
|-------|-----------|---------------|
| RPM (`0x19 00`) | 10 LEDs (legacy), 18 LEDs (CS Pro / KS Pro) | 2 chunks for ≤10, 4 chunks for 18 (last padded) |
| Button (`0x19 01`) | 14 (VGS) / 8 (CS V2.1) / varies | 3 chunks (last padded) |

**Padding rule:** unused entries within a chunk MUST use index `0xFF`. Zero
padding (`00 00 00 00`) is interpreted as "set LED 0 to black" by firmware,
causing button 0 to flicker on every frame. See
[`Devices/MozaLedDeviceManager.cs:472`](../../../Devices/MozaLedDeviceManager.cs)
(`SendColorChunks`).

### Bitmask format

Selects which LEDs are currently lit. Builder logic in
[`Devices/MozaLedDeviceManager.cs:450`](../../../Devices/MozaLedDeviceManager.cs)
(`BuildRpmBitmaskBytes`):

| LED count | Bitmask size | Wire bytes |
|-----------|--------------|------------|
| ≤16 | 2 bytes (LE u16) | `[lo, hi]` |
| 17+ (CS Pro, KS Pro) | 4 bytes (LE u32) | `[b0, b1, b2, b3]` |

Bit `i` lit ↔ LED `i` has non-black color in the chunk write. Plugin sends
the bitmask only when it changes (or every frame when
`AlwaysResendBitmask` is set), regardless of color-chunk cadence.

### Example (CS V2.1 — 10 RPM LEDs, alternating red/blue)

Color chunks (2 × 20-byte):

```
chunk 0 (LEDs 0..4):
  7E 14 3F 17 19 00
    00 FF 00 00   01 00 00 FF   02 FF 00 00   03 00 00 FF   04 FF 00 00
  [chk]
chunk 1 (LEDs 5..9):
  7E 14 3F 17 19 00
    05 00 00 FF   06 FF 00 00   07 00 00 FF   08 FF 00 00   09 00 00 FF
  [chk]
```

Bitmask (all 10 lit):

```
7E 04 3F 17 1A 00 FF 03 [chk]      # 0x03FF = 10 bits set
```

### Wheel echo

Both write commands echo verbatim — see
[`../wire/wheel-write-echoes.md`](../wire/wheel-write-echoes.md) entries for
prefixes `19 00`, `19 01`, `1A 00`, `1A 01` (group `0x3F`, dev `0x17`).

### Static (settings) vs live (telemetry) paths

Groups `0x19`/`0x1A` are the **live** path: per-frame writes that render
only while telemetry is active (`wheel-telemetry-mode != 2`). The static
path uses cmd `0x1F [G] FF [N]` to persist a per-LED color in EEPROM (see
[`../devices/wheel-0x17.md` § Extended LED Group Architecture](../devices/wheel-0x17.md)).
The two pipelines coexist: static colors render in idle mode; live colors
override while a frame is feeding the bitmask.
