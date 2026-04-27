## H-Pattern Shifter (Device `0x1A` / 26)

### Group `0x51` / `0x52` (81 / 82) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| hid-mode | `01` | 2 | int | |
| shifter-type | `02` | 2 | int | |
| direction | `05` | 2 | int | |
| paddle-sync | `06` | 2 | int | |

### Group `0x53` (83) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output-x | `01` | 2 | int | |
| output-y | `02` | 2 | int | |

### Group `0x54` (84) — Calibration (write-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| calibration-start | `03` | 2 | int | |
| calibration-stop | `04` | 2 | int | |

---

## Sequential Shifter (Device `0x1A` / 26)

Shares device ID `0x1A` and group numbers with the H-pattern shifter. Distinguish by command IDs or the `shifter-type` setting.

### Group `0x51` / `0x52` (81 / 82) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| hid-mode | `01` | 2 | int | |
| shifter-type | `02` | 2 | int | |
| brightness | `03` | 2 | int | |
| colors | `04` | 2 | array | |
| direction | `05` | 2 | int | |
| paddle-sync | `06` | 2 | int | |

### Group `0x53` (83) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output-x | `01` | 2 | int | |
| output-y | `02` | 2 | int | |
