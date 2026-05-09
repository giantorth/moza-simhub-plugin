## Handbrake (Device `0x1B` / 27)

### Group `0x5B` / `0x5C` (91 / 92) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| direction | `01` | 2 | int | |
| min | `02` | 2 | int | |
| max | `03` | 2 | int | |
| hid-mode | `04` | 2 | int | |
| y1 | `05` | 4 | float | Curve point |
| y2 | `06` | 4 | float | |
| y3 | `07` | 4 | float | |
| y4 | `08` | 4 | float | |
| y5 | `09` | 4 | float | |
| button-threshold | `0A` | 2 | int | |
| mode | `0B` | 2 | int | |

### Group `0x5D` (93) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output | `01` | 2 | int | |

### Group `0x5E` (94) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| calibration-start | `03` | 2 | int | |
| calibration-stop | `04` | 2 | int | |
