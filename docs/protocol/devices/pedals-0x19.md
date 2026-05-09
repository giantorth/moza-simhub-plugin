## Pedals (Device `0x19` / 25)

### Group `0x23` / `0x24` (35 / 36) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| throttle-dir | `01` | 2 | int | |
| throttle-min | `02` | 2 | int | |
| throttle-max | `03` | 2 | int | |
| brake-dir | `04` | 2 | int | |
| brake-min | `05` | 2 | int | |
| brake-max | `06` | 2 | int | |
| clutch-dir | `07` | 2 | int | |
| clutch-min | `08` | 2 | int | |
| clutch-max | `09` | 2 | int | |
| compat-mode | `0D` | 2 | int | |
| throttle-y1 | `0E` | 4 | float | Curve points — spline knots for pedal response shaping |
| throttle-y2 | `0F` | 4 | float | |
| throttle-y3 | `10` | 4 | float | |
| throttle-y4 | `11` | 4 | float | |
| throttle-y5 | `1B` | 4 | float | |
| brake-y1 | `12` | 4 | float | |
| brake-y2 | `13` | 4 | float | |
| brake-y3 | `14` | 4 | float | |
| brake-y4 | `15` | 4 | float | |
| brake-y5 | `1C` | 4 | float | |
| clutch-y1 | `16` | 4 | float | |
| clutch-y2 | `17` | 4 | float | |
| clutch-y3 | `18` | 4 | float | |
| clutch-y4 | `19` | 4 | float | |
| clutch-y5 | `1D` | 4 | float | |
| brake-angle-ratio | `1A` | 4 | float | |
| throttle-hid-source | `1E` | 2 | int | |
| throttle-hid-cmd | `1F` | 2 | int | |

### Group `0x25` (37) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| throttle-output | `01` | 2 | int | |
| brake-output | `02` | 2 | int | |
| clutch-output | `03` | 2 | int | |

### Group `0x26` (38) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| throttle-calibration-start | `0C` | 2 | int | |
| brake-calibration-start | `0D` | 2 | int | |
| clutch-calibration-start | `0E` | 2 | int | |
| throttle-calibration-stop | `10` | 2 | int | |
| brake-calibration-stop | `11` | 2 | int | |
| clutch-calibration-stop | `12` | 2 | int | |
