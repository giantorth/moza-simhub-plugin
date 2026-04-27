### Post-upload / active display cycle (group 0x43)

Sent ~1/s after dashboard active, interleaved per page.

**`7c:27` periodic display config** — two payloads per page, cycling through all pages. Page-derived values confirmed across 1-page (rpm-only) and 3-page (F1) dashboards:

| Page `p` | 8-byte payload | 4-byte payload |
|-----------|---------------|---------------|
| 0 | `0f 80 05 00 03 00 fe 01` | `0f 00 06 00` |
| 1 | `0f 80 07 00 05 00 fe 01` | `0f 00 08 00` |
| 2 | `0f 80 09 00 07 00 fe 01` | `0f 00 0a 00` |
| Formula | `0f 80 (5+2p) 00 (3+2p) 00 fe 01` | `0f 00 (6+2p) 00` |

Bytes `0f`, `80`/`00`, `fe 01` constant. Page count = mzdash `children` array length.

**`7c:23` dashboard activate** — sent alongside `7c:27`, one of each per page. Declares active pages:

| Page `p` | 8-byte payload |
|-----------|---------------|
| 0 | `46 80 07 00 05 00 fe 01` |
| 1 | `46 80 09 00 07 00 fe 01` |
| 2 | `46 80 0b 00 09 00 fe 01` |
| Formula | `46 80 (7+2p) 00 (5+2p) 00 fe 01` |

Bytes `46`, `80`, `fe 01` constant. No second short-form frame (unlike `7c:27`). Wheel→host direction (group 0xC3) uses `7c:23` with different byte layout to advertise channel catalog before session opens — see [`../tier-definition/`](../tier-definition/).

**`7c:1e` display settings push** — sent by Pithouse to all wheel models (not VGS-specific). Brightness, timeout, orientation. Same structure as `7c:23`/`7c:27` with constant byte `6c`:

| Observed payload | Context |
|------------------|---------|
| `6c 80 0c 00 0a 00 fe 01` | With active dashboard pages (7c:27/7c:23 also cycling) |
| `6c 80 06 00 04 00 fe 01` | After dashboard switch / settings change (7c:27/7c:23 stop) |

b2/b4 values are sequence counters (same as 7c:27/7c:23), not display settings. Actual brightness/timeout values written via `grp 0x40` settings commands (`cmd 0x1b` = brightness, `cmd 0x1e` = timeout).
