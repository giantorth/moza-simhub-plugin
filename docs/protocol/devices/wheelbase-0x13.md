## Wheelbase (Device `0x13` / 19)

### Group `0x28` / `0x29` (40 / 41) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| limit | `01` | 2 | int | Steering angle limit |
| ffb-strength | `02` | 2 | int | |
| inertia | `04` | 2 | int | |
| damper | `07` | 2 | int | |
| friction | `08` | 2 | int | |
| spring | `09` | 2 | int | |
| speed | `0A` | 2 | int | |
| road-sensitivity | `0C` | 2 | int | |
| protection | `0D` | 2 | int | Hands-off protection strength |
| protection-mode | `2D` | 2 | int | |
| equalizer1 | `0E` | 2 | int | |
| equalizer2 | `0F` | 2 | int | |
| equalizer3 | `10` | 2 | int | |
| equalizer4 | `11` | 2 | int | |
| equalizer5 | `14` | 2 | int | |
| equalizer6 | `2C` | 2 | int | |
| torque | `12` | 2 | int | |
| natural-inertia | `13` | 2 | int | Hands-off protection |
| natural-inertia-enable | `16` | 2 | int | |
| max-angle | `17` | 2 | int | |
| ffb-reverse | `18` | 2 | int | |
| speed-damping | `19` | 2 | int | |
| speed-damping-point | `1A` | 2 | int | |
| soft-limit-strength | `1B` | 2 | int | |
| soft-limit-retain | `1C` | 2 | int | |
| soft-limit-stiffness | `1F` | 2 | int | |
| temp-strategy | `1E` | 2 | int | |
| ffb-curve-x1 | `22 01` | 1 | int | FFB linearization curve X point 1 |
| ffb-curve-x2 | `22 02` | 1 | int | |
| ffb-curve-x3 | `22 03` | 1 | int | |
| ffb-curve-x4 | `22 04` | 1 | int | |
| ffb-curve-y1 | `22 05` | 1 | int | |
| ffb-curve-y2 | `22 06` | 1 | int | |
| ffb-curve-y3 | `22 07` | 1 | int | |
| ffb-curve-y4 | `22 08` | 1 | int | |
| ffb-curve-y5 | `22 09` | 1 | int | |
| ffb-curve-y0 | `22 0A` | 1 | int | No read or write group (both -1) — not usable |
| ffb-disable | `FE` | 2 | int | |

### Group `0x2A` (42) — Calibration / Music (startup chime)

Group 42 is used for both writes (calibration, music set) and reads (music get). `Dir` column applies.

> **Capture-verified** (2026-05-05) from R25 base. See `usb-capture/startupchime/`.

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| calibration | `01` | W | 2 | int | |
| music-preview | `43 00` | W | 1 | int | Plays chime at index once (immediate audible feedback) |
| music-index-set | `43 01` | W | 1 | int | Persists selected startup chime (1–10) |
| music-index-get | `43 02` | R | 1 | int | Returns active chime index |
| music-enabled-set | `43 03` | W | 1 | int | 0 = disable, 1 = enable startup chime |
| music-enabled-get | `43 04` | R | 1 | int | Returns enabled state |
| music-volume-set | `44 00` | W | 1 | int | 0x00 (mute) – 0xFF (max) |
| music-volume-get | `44 01` | R | 1 | int | Returns current volume |

**Chime index range:** 1–10 (0x01–0x0A). 10 built-in chimes on R25 base.

**Default volume:** 0x17 (23 decimal, ~9%).

**PitHouse workflow:** read state → `music-index-set(N)` → `music-preview(N)` → periodic re-read.

**Frame example — set chime 5 and preview:**
```
7E 03 2A 13 43 01 05 [chk]   ← index-set = 5
7E 03 2A 13 43 00 05 [chk]   ← preview chime 5
```
Both produce ACK response: `7E 03 AA 31 43 0x 05 [chk]` (echo with group|0x80, device nibble-swapped).

### Group `0x2B` (43) — Status (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| state | `01` | 2 | int | |
| state-err | `02` | 2 | int | |
| mcu-temp | `04` | 2 | int | |
| mosfet-temp | `05` | 2 | int | |
| motor-temp | `06` | 2 | int | |

### Group `0x2D` (45) — Sequence Counter (write-only)

Observed in USB capture at ~50×/sec during driving. Group 45 is not in the main command list — discovered by capture analysis. See [`../telemetry/control-signals.md` § Sequence counter](../telemetry/control-signals.md).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| sequence-counter | `F5 31` | 4 | int | Last byte monotonically increments each send; likely a frame sync counter sent to base |
