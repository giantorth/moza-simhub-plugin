## Base ambient LED control (groups `0x20` write / `0x22` read)

Two LED strips of 9 LEDs each on the wheelbase body, controlled via
write group `0x20` (32) and read group `0x22` (34). Sent to the main
controller at dev `0x12`.

> **Source:** rs21_parameter.db + USB captures from R25 base (2026-05-05).
> See `usb-capture/startupchime/` — "Moza R25 Wheel Base Settings Part 1/2"
> contain full LED read/write sequences.

### Frame layout

```
7E [N] 20 12 [cmd] [value bytes] [checksum]
```

| Group | Direction | Cmd ID range | Notes |
|-------|-----------|--------------|-------|
| `0x20` (32) | host → device | per-cmd (see table) | Write — sets ambient LED state |
| `0x22` (34) | host → device | per-cmd | Read — returns currently stored value |

Read responses use `0xA0` / `0xA2` (group | 0x80) and `0x21` (nibble-swap
of `0x12`).

### Per-command summary

Full table in
[`../devices/main-hub-0x12.md` § Group `0x20` / `0x22`](../devices/main-hub-0x12.md).
Selected commands:

| Command | Cmd ID | Bytes | Type | Value semantics |
|---------|--------|-------|------|-----------------|
| `indicator-state` | `1C` | 1 | int | On (1) / off (0) |
| `standby-mode` | `1D` | 1 | int | 0 = constant, 1 = ?, 2 = breath, 3 = cycle, 4 = rainbow, 5 = flow |
| `standby-interval` | `1E [mode]` | 2 | int | Per-mode interval in ms (big-endian u16). Each mode stores its own interval independently |
| `brightness` | `1F FF` | 1 | int | 0..255. DB lists `1F 02` but PitHouse uses `1F FF` on wire |
| `led-color` | `20 [strip] [mode] [led]` | 3 | array (RGB) | strip=0/1, mode=1 (constant) / 2 (breath), led=0..8 |
| `sleep-mode` | `21` | 1 | int | |
| `sleep-timeout` | `22` | 2 | int | |
| `breath-interval` | `23 01` | 2 | int | Observed 0x0BB8 (3000ms) — may be global breath speed |
| `sleep-led-color` | `25 [strip] 01 [led]` | 3 | array (RGB) | Per-LED breathing color in sleep |
| `startup-color` | `26` | 3 | array (RGB) | Color shown briefly at power-on |
| `shutdown-color` | `27` | 3 | array (RGB) | Color shown at power-off |

### Standby mode details

Observed default intervals from R25 base:

| Mode | Name | Default interval | Notes |
|------|------|-----------------|-------|
| 0 | Constant | — | Static color, no animation |
| 1 | *(unknown)* | — | PitHouse sends it; effect unconfirmed |
| 2 | Breath | 3000ms (0x0BB8) | Fade in/out cycle |
| 3 | Cycle | 1000ms (0x03E8) | Color rotation |
| 4 | Rainbow | 1714ms (0x06B2) | Spectrum sweep |
| 5 | Flow | 100ms (0x0064) | Directional flow animation |

Interval is written independently of mode selection. Setting `1E 02 13 88`
changes breath interval to 5000ms without switching the active mode.

### Observed values from R25 base

| Setting | Value | Interpretation |
|---------|-------|----------------|
| indicator-state | `01` | LEDs on |
| standby-mode | `04` | Rainbow |
| brightness | `64` | 100 decimal (~39%) |
| sleep-mode | `01` | Enabled |
| sleep-timeout | `00 0F` | 15 |
| startup-color | `66 B8 FF` | #66B8FF (light blue) |
| shutdown-color | `66 B8 FF` | #66B8FF (same) |
| all led-color | `56 F7 FC` | #56F7FC (cyan), both strips/modes |

### Worked example: set strip 0 LED 4 to magenta in constant mode

```
7E 06 20 12 20 00 01 04 FF 00 FF [chk]
                │  │  │  │  │  │  │
                │  │  │  │  │  │  └ B
                │  │  │  │  │  └─── G
                │  │  │  │  └────── R
                │  │  │  └───────── led index = 4
                │  │  └──────────── mode = 1 (constant)
                │  └─────────────── strip = 0
                └────────────────── cmd = 0x20 (led-color)
```

### Worked example: set standby mode to breath with 5000ms interval

```
7E 02 20 12 1D 02 [chk]          ← mode = breath
7E 04 20 12 1E 02 13 88 [chk]    ← breath interval = 5000ms
                │  │  │  │
                │  │  └──┴─── interval = 0x1388 = 5000ms (BE u16)
                │  └────────── mode byte = 02 (targets breath register)
                └───────────── cmd = 0x1E (standby-interval)
```

### Write response behavior

Write-group commands (`0x20`) produce **two** responses:
1. Immediate ACK on group `0xA0` (write-group | 0x80) echoing the payload
2. Follow-up notification on group `0xA2` (read-group | 0x80) with the stored value

This dual-response may serve as firmware-level change notification to other
listeners on the bus.

### Live telemetry commands (RPM indicator)

During game telemetry, PitHouse drives the base LEDs as an RPM bar using
two live commands — mirroring the wheel LED `0x19`/`0x1A` pair but with
cmd bytes `0x1A`/`0x1B`.

Each strip (0 and 1) is independently addressable with 9 LEDs each
(18 total). In the observed capture, PitHouse sends identical data to
both strips (mirrored RPM bar), but this is an application choice.

> **Source:** USB capture `usb-capture/startupchime/R25 LED Telemetry.pcapng`.

#### `0x1A` — live-color-chunk

Sets per-LED colors. Same 4-byte-per-entry format as wheel live-colors
(`0x19`):

```
7E [N] 20 12 1A [strip] [idx₀ R G B] [idx₁ R G B] ... [chk]
```

Each chunk carries up to 5 entries (20 bytes of LED data). Two chunks per
strip cover all 9 LEDs:
- Chunk 1: LEDs 0–4 (N=22: cmd + strip + 5×4)
- Chunk 2: LEDs 5–8 (N=18: cmd + strip + 4×4)

Colors are only re-sent when the palette changes — not every frame.

**Observed RPM color gradient:**

| RPM region | Color | Hex |
|------------|-------|-----|
| Low | Green | `#00FF00` |
| Mid | Yellow | `#FFFF00` |
| High | Red | `#FF0000` |
| Over-rev | Magenta | `#FF00FF` |
| Off / unlit | Black | `#000000` |

LEDs fill from index 0 upward. The palette shifts across the strip —
green on lower LEDs, red in the middle, magenta at the top during
over-rev.

**Example: 6 LEDs lit, mixed green/red:**
```
7E 16 20 12 1A 00 00 00FF00 01 00FF00 02 00FF00 03 FF0000 04 FF0000 [chk]
                │  │  │       │        │         │         └ LED 4: red
                │  │  │       │        │         └ LED 3: red
                │  │  │       │        └ LED 2: green
                │  │  │       └ LED 1: green
                │  │  └ LED 0: green
                │  └─── strip = 0
                └────── cmd = 0x1A (live-color)

7E 12 20 12 1A 00 05 FF0000 06 000000 07 000000 08 000000 [chk]
                      └ LED 5: red, LEDs 6–8: off (beyond bitmask)
```

#### `0x1B` — live-bitmask

Controls which LEDs are active. Sent every frame to both strips:

```
7E 06 20 12 1B [strip] [u32le bitmask] [chk]
```

Bits 0–8 correspond to LEDs 0–8 (9 LEDs per strip).

**Bitmask progression (RPM ramp-up):**

| Mask | Binary | LEDs lit |
|------|--------|----------|
| `0x0000` | `000000000` | 0 (idle) |
| `0x0001` | `000000001` | 1 |
| `0x0003` | `000000011` | 2 |
| `0x0007` | `000000111` | 3 |
| `0x000F` | `000001111` | 4 |
| `0x001F` | `000011111` | 5 |
| `0x003F` | `000111111` | 6 |
| `0x007F` | `001111111` | 7 |
| `0x00FF` | `011111111` | 8 |
| `0x01FF` | `111111111` | 9 (all — redline) |

#### Telemetry send cadence

- Bitmask (`0x1B`): every frame, both strips (~10 Hz observed)
- Colors (`0x1A`): only on palette change (not every frame)
- Each strip addressed independently (PitHouse mirrors both in this capture)

### Group `0x1F` (31) — Hub status reads

Polled alongside the LED telemetry at ~10 Hz. Read-only, device `0x12`,
responses on group `0x9F` device `0x21`.

| Cmd | Request | Response | Notes |
|-----|---------|----------|-------|
| `4F 08` | `4F 08 00` | `4F 08 FF 00` | Status register (constant in capture) |
| `4F 09` | `4F 09 00` | `4F 09 FF 00` | Status register |
| `4F 0A` | `4F 0A 00` | `4F 0A FF 00` | Status register |
| `4F 0B` | `4F 0B 00` | `4F 0B FF 00` | Status register |
| `4D` | `4D` | `4D 64` | Possibly brightness readback (0x64 = 100) |
| `0A` | `0A` | `0A 00` | Init-time query (sent once) |
| `0F` | `0F` | `0F 00` | Init-time query (sent once) |

### Why two groups for one feature

The dual-group `0x20` / `0x22` split (write vs read) follows the same
convention as group `0x28`/`0x29` for the wheelbase settings (read /
write split with shared cmd IDs). Read group has `set-` cmds removed and
returns currently-stored value of the corresponding write.

### Plugin status

Plugin does **not** drive these LEDs — no command wiring for any of the
above. If a future SimHub feature (e.g. "ambient base feedback") needs
them, send via `MozaDeviceManager.WriteSetting("base-led-...", value)`
once the commands are added to `MozaCommandDatabase`.
