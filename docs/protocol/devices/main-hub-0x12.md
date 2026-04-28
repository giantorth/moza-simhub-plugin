## Main (Device `0x12` / 18)

### Group `0x1E` (30) — Output (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| output | `39` | 7 | int | |

### Group `0x1F` (31) — Settings

Note: get and set commands in this group use **different** command IDs (unlike most other devices where read/write share the same ID).

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| set-compat-mode | `13` | W | 1 | int | |
| get-compat-mode | `17` | R | 1 | int | |
| get-ble-mode | `46` | R | 1 | int | 0 = off, 0x55 = on |
| set-ble-mode | `47` | W | 1 | int | |
| get-led-status | `08` | R | 1 | int | |
| set-led-status | `09` | W | 1 | int | |
| get-work-mode | `34` | R | 1 | int | |
| set-work-mode | `33` | W | 1 | int | |
| get-default-ffb-status | `36` | R | 1 | int | |
| set-default-ffb-status | `35` | W | 1 | int | |
| get-interpolation | `4D` | R | 1 | int | |
| set-interpolation | `4C` | W | 1 | int | |
| get-spring-gain | `4F 08` | R | 1 | int | |
| set-spring-gain | `4E 08` | W | 1 | int | |
| get-damper-gain | `4F 09` | R | 1 | int | |
| set-damper-gain | `4E 09` | W | 1 | int | |
| get-inertia-gain | `4F 0A` | R | 1 | int | |
| set-inertia-gain | `4E 0A` | W | 1 | int | |
| get-friction-gain | `4F 0B` | R | 1 | int | |
| set-friction-gain | `4E 0B` | W | 1 | int | |

### Group `0x20` / `0x22` (32 / 34) — Base Ambient LEDs

Controls 2 LED strips (9 LEDs each) on the wheelbase body. Group 32 = write, group 34 = read. Found in rs21_parameter.db but not observed in USB captures. See [`../leds/base-ambient-0x20-0x22.md`](../leds/base-ambient-0x20-0x22.md).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| indicator-state | `1C` | 1 | int | On/off |
| standby-mode | `1D` | 1 | int | 0=constant, 2=breath, 3=cycle, 4=rainbow, 5=flow |
| standby-interval | `1E [mode]` | 2 | int | Interval for given mode |
| brightness | `1F 02` | 1 | int | |
| led-color | `20 [strip] [mode] [led]` | 3 | array | RGB. strip=0/1, mode=1(constant)/2(breath), led=0–8 |
| sleep-mode | `21` | 1 | int | |
| sleep-timeout | `22` | 2 | int | |
| sleep-breath-interval | `23 01` | 2 | int | |
| sleep-brightness | `24` | 1 | int | |
| sleep-led-color | `25 [strip] 01 [led]` | 3 | array | Sleep breathing per-LED RGB |
| startup-color | `26` | 3 | array | RGB |
| shutdown-color | `27` | 3 | array | RGB |

## Hub (Device `0x12` / 18)

### Group `0x64` (100) — Connected Device Status (read-only)

Two probe forms observed:

**Form A — single-byte cmd ID** (SimHub plugin, also `sim/wheel_sim.py` `(0x64, 0x12, 03)` "hub-port1-power probe"):

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| base | `02` | 2 | int | |
| port1 | `03` | 2 | int | |
| port2 | `04` | 2 | int | |
| port3 | `05 01` | 1 | int | |
| pedals1 | `06` | 2 | int | |
| pedals2 | `07` | 2 | int | |
| pedals3 | `08` | 2 | int | |

**Form B — `01 NN 00` PitHouse probe** (observed in `usb-capture/ksp/gfdsgfd.pcapng` @ f54501, 5-frame burst):

| Request | Response (`0xE4/0x21`) | Notes |
|---------|------------------------|-------|
| `7E 03 64 12 01 01 00` | `7E 03 E4 21 01 01 00` | Slot 1 — value `00` = empty/none |
| `7E 03 64 12 01 02 00` | `7E 03 E4 21 01 02 07` | Slot 2 — `07` |
| `7E 03 64 12 01 03 00` | `7E 03 E4 21 01 03 00` | Slot 3 |
| `7E 03 64 12 01 04 00` | `7E 03 E4 21 01 04 00` | Slot 4 |
| `7E 03 64 12 01 05 00` | `7E 03 E4 21 01 05 02` | Slot 5 — `02` |

Sub-cmd `01 NN` enumerates 5 slots; response last byte = device-type/status code. Distinct from Form A — PitHouse uses Form B in startup probe. Per-slot semantics (which physical port maps to which NN) undecoded.
