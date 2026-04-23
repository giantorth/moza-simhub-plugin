# Moza Racing Serial Command Reference

Device IDs and command definitions for the Moza Racing serial protocol.
See [moza-protocol.md](moza-protocol.md) for frame format, checksum algorithm, response encoding, and observed protocol behavior from USB captures.

## Protocol Constants

| Name | Dec | Hex | Notes |
|------|-----|-----|-------|
| Frame start byte | 126 | `0x7E` | First byte of every frame |
| Checksum magic | 13 | `0x0D` | Added to the running byte sum before mod 256 |

## Device IDs

All communication goes over a single COM serial interface. Device IDs are addresses on the internal bus — the wheelbase hub routes each frame to the correct peripheral.

| Device | Dec | Hex | Notes |
|--------|-----|-----|-------|
| main / hub | 18 | `0x12` | Base USB address; hub enumeration shares this ID |
| base | 19 | `0x13` | Wheelbase motor controller |
| dash | 20 | `0x14` | Dashboard display |
| wheel | 21 | `0x15` | Secondary wheel address — observed in group 0x43 broadcasts, purpose unclear |
| wheel | 23 | `0x17` | Primary steering wheel address used by all known models |
| pedals | 25 | `0x19` | Pedal set |
| hpattern / sequential | 26 | `0x1A` | H-pattern and sequential shifter share this device ID |
| handbrake | 27 | `0x1B` | |
| estop | 28 | `0x1C` | Emergency stop button |

Response device IDs have their nibbles swapped: base `0x13` → response `0x31`, wheel `0x17` → `0x71`, etc. Response group IDs have `0x80` added. See [moza-protocol.md](moza-protocol.md) for full response encoding rules.

---

## Command Table Format

Each device section is organized by group. Within a group, the **ID** column shows the command ID bytes (1–4 bytes, big-endian hex). **Bytes** is the payload size (value bytes only, not the ID). **Dir** is `R` (read-only), `W` (write-only), or `RW` (both, using the same group).

When a command is truly read/write and uses the same ID in both directions, the group is shown as `read / write`. When read and write use the same group number, `Dir` disambiguates.

---

## EEPROM Direct Access (Group `0x0A` / 10 — any device)

Low-level EEPROM read/write protocol, applicable to any device. Bypasses the named command interface. Found in rs21_parameter.db but not observed in USB captures. See [moza-protocol.md § EEPROM direct access](moza-protocol.md#eeprom-direct-access-group-0x0a--10).

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| select-table | `00 05` | W | 4 | int | Select EEPROM table ID |
| read-table | `00 06` | R | 4 | int | Read selected table ID |
| select-address | `00 07` | W | 4 | int | Select address within table |
| read-address | `00 08` | R | 4 | int | Read selected address |
| write-int | `00 09` | W | 4 | int | Write int at selected table+address |
| read-int | `00 0A` | R | 4 | int | Read int at selected table+address |
| write-float | `00 0B` | W | 4 | float | Write float at selected table+address |
| read-float | `00 0C` | R | 4 | float | Read float at selected table+address |

Known EEPROM tables: 2=Base (38 params), 3=Motor (76 params), 4=Wheel (123 params), 5=Pedals (45 params), 11=Unknown (8 params).

---

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

Controls 2 LED strips (9 LEDs each) on the wheelbase body. Group 32 = write, group 34 = read. Found in rs21_parameter.db but not observed in USB captures. See [moza-protocol.md § base ambient LED control](moza-protocol.md#base-ambient-led-control-groups-0x200x22--3234).

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

---

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

---

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

### Group `0x2A` (42) — Calibration / Music

Group 42 is used for both writes (calibration, music set) and reads (music get). `Dir` column applies.

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| calibration | `01` | W | 2 | int | |
| music-preview | `43 00` | W | 1 | int | |
| music-index-set | `43 01` | W | 1 | int | |
| music-index-get | `43 02` | R | 1 | int | |
| music-enabled-set | `43 03` | W | 1 | int | |
| music-enabled-get | `43 04` | R | 1 | int | |
| music-volume-set | `44 00` | W | 1 | int | |
| music-volume-get | `44 01` | R | 1 | int | |

### Group `0x2B` (43) — Status (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| state | `01` | 2 | int | |
| state-err | `02` | 2 | int | |
| mcu-temp | `04` | 2 | int | |
| mosfet-temp | `05` | 2 | int | |
| motor-temp | `06` | 2 | int | |

### Group `0x2D` (45) — Sequence Counter (write-only)

Observed in USB capture at ~50×/sec during driving. Group 45 is not in the main command list — discovered by capture analysis. See [moza-protocol.md § sequence counter](moza-protocol.md#sequence-counter-group-0x2d-device-0x13-50-hz).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| sequence-counter | `F5 31` | 4 | int | Last byte monotonically increments each send; likely a frame sync counter sent to base |

---

## Standalone Dash Display — MDD (Device `0x14` / 20)

> **This device is the external Moza MDD peripheral** — a separate physical unit, distinct from steering wheels with integrated display screens (device `0x17`). All captured live telemetry targets device `0x17`, not the MDD; whether the MDD uses the same protocol is unknown.

### Group `0x32` / `0x33` (50 / 51) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-timings | `05` | 10 | array | |
| rpm-display-mode | `07` | 1 | int | |
| flag-colors | `08 00` | 18 | array | Write-only |
| rpm-blink-color1 | `09 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `09 01` | 3 | array | |
| rpm-blink-color3 | `09 02` | 3 | array | |
| rpm-blink-color4 | `09 03` | 3 | array | |
| rpm-blink-color5 | `09 04` | 3 | array | |
| rpm-blink-color6 | `09 05` | 3 | array | |
| rpm-blink-color7 | `09 06` | 3 | array | |
| rpm-blink-color8 | `09 07` | 3 | array | |
| rpm-blink-color9 | `09 08` | 3 | array | |
| rpm-blink-color10 | `09 09` | 3 | array | |
| rpm-brightness | `0A 00` | 1 | int | |
| flags-brightness | `0A 02` | 1 | int | |
| rpm-color1 | `0B 00 00` | 3 | array | RGB |
| rpm-color2 | `0B 00 01` | 3 | array | |
| rpm-color3 | `0B 00 02` | 3 | array | |
| rpm-color4 | `0B 00 03` | 3 | array | |
| rpm-color5 | `0B 00 04` | 3 | array | |
| rpm-color6 | `0B 00 05` | 3 | array | |
| rpm-color7 | `0B 00 06` | 3 | array | |
| rpm-color8 | `0B 00 07` | 3 | array | |
| rpm-color9 | `0B 00 08` | 3 | array | |
| rpm-color10 | `0B 00 09` | 3 | array | |
| flag-color1 | `0B 02 00` | 3 | array | |
| flag-color2 | `0B 02 01` | 3 | array | |
| flag-color3 | `0B 02 02` | 3 | array | |
| flag-color4 | `0B 02 03` | 3 | array | |
| flag-color5 | `0B 02 04` | 3 | array | |
| flag-color6 | `0B 02 05` | 3 | array | |
| rpm-mode | `0D` | 1 | int | |
| rpm-value1 | `0E 00` | 4 | int | RPM threshold for LED 1 |
| rpm-value2 | `0E 01` | 4 | int | |
| rpm-value3 | `0E 02` | 4 | int | |
| rpm-value4 | `0E 03` | 4 | int | |
| rpm-value5 | `0E 04` | 4 | int | |
| rpm-value6 | `0E 05` | 4 | int | |
| rpm-value7 | `0E 06` | 4 | int | |
| rpm-value8 | `0E 07` | 4 | int | |
| rpm-value9 | `0E 08` | 4 | int | |
| rpm-value10 | `0E 09` | 4 | int | |
| rpm-indicator-mode | `11 00` | 1 | int | 0=Off, 1=RPM, 2=On |
| rpm-interval | `0C` | 4 | int | |
| flags-indicator-mode | `11 02` | 1 | int | 0=Off, 1=Flags, 2=On |

---

## Steering Wheel (Device `0x17` / 23)

This covers all Moza steering wheels, including models with integrated display screens (e.g. formula-style wheels that show speed, gear, lap time). Live game telemetry (group `0x43`) is sent here by Pithouse — confirmed by USB capture. Wheels with integrated displays use that data to drive the screen internally. See [moza-protocol.md](moza-protocol.md) for the full topology and telemetry analysis.

### Identity Queries (read-only)

Request payload is just the command ID byte with no value bytes. The device returns 16 null-padded ASCII bytes regardless of request size. See [moza-protocol.md § wheel connection probe sequence](moza-protocol.md#wheel-connection-probe-sequence).

| Command | Read Group | ID | Notes |
|---------|------------|----|-------|
| model-name | `0x07` | `01` | e.g. `VGS`, `CS V2.1` (see [moza-protocol.md](moza-protocol.md#known-wheel-model-names)) |
| hw-version | `0x08` | `01` | e.g. `RS21-W08-HW SM-C` |
| hw-revision | `0x08` | `02` | e.g. `U-V12`, `U-V02` |
| sw-version | `0x0F` | `01` | Firmware version string |
| serial-a | `0x10` | `00` | Serial number first 16 chars |
| serial-b | `0x10` | `01` | Serial number second 16 chars |

Full serial number = serial-a + serial-b (32 ASCII chars total).

### Group `0x3F` / `0x40` (63 / 64) — Configuration

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| colors | `00` | 15 | hex | Write-only |
| brightness | `01` | 1 | int | |
| rpm-timings | `02` | 10 | array | |
| paddles-mode | `03` | 1 | int | 1=Buttons, 2=Combined, 3=Split (1-based) |
| stick-mode | `05` | 2 | int | 0=Buttons, 256=D-Pad |
| set-rpm-display-mode | `07` | 1 | int | Write-only |
| get-rpm-display-mode | `08` | 1 | int | Read-only |
| clutch-point | `09` | 1 | int | |
| knob-mode | `0A` | 1 | int | |
| paddle-adaptive-mode | `0B` | 1 | int | |
| paddle-button-mode | `0D` | 1 | int | |
| flag-colors1 | `0E 00` | 21 | array | Write-only |
| flag-colors2 | `0E 01` | 9 | array | Write-only |
| rpm-blink-color1 | `0F 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `0F 01` | 3 | array | |
| rpm-blink-color3 | `0F 02` | 3 | array | |
| rpm-blink-color4 | `0F 03` | 3 | array | |
| rpm-blink-color5 | `0F 04` | 3 | array | |
| rpm-blink-color6 | `0F 05` | 3 | array | |
| rpm-blink-color7 | `0F 06` | 3 | array | |
| rpm-blink-color8 | `0F 07` | 3 | array | |
| rpm-blink-color9 | `0F 08` | 3 | array | |
| rpm-blink-color10 | `0F 09` | 3 | array | |
| key-combination | `13` | 4 | array | |
| telemetry-mode | `1C 00` | 1 | int | |
| telemetry-idle-effect | `1D 00` | 1 | int | |
| buttons-idle-effect | `1D 01` | 1 | int | |
| telemetry-idle-interval | `1E 00` | 3 | int | Write-only |
| buttons-idle-interval | `1E 01` | 3 | int | Write-only |
| idle-mode | `20` | 1 | int | |
| idle-timeout | `21` | 2 | int | |
| idle-color | `24 FF 01 FF` | 3 | array | |
| idle-speed | `22 00` | 2 | int | |
| rpm-idle-speed | `24 00 05` | 2 | int | |
| rpm-interval | `16` | 4 | int | |
| rpm-mode | `17` | 1 | int | |
| rpm-value1 | `18 00` | 2 | int | RPM threshold for LED 1 |
| rpm-value2 | `18 01` | 2 | int | |
| rpm-value3 | `18 02` | 2 | int | |
| rpm-value4 | `18 03` | 2 | int | |
| rpm-value5 | `18 04` | 2 | int | |
| rpm-value6 | `18 05` | 2 | int | |
| rpm-value7 | `18 06` | 2 | int | |
| rpm-value8 | `18 07` | 2 | int | |
| rpm-value9 | `18 08` | 2 | int | |
| rpm-value10 | `18 09` | 2 | int | |
| rpm-color1 | `1F 00 FF 00` | 3 | array | RGB |
| rpm-color2 | `1F 00 FF 01` | 3 | array | |
| rpm-color3 | `1F 00 FF 02` | 3 | array | |
| rpm-color4 | `1F 00 FF 03` | 3 | array | |
| rpm-color5 | `1F 00 FF 04` | 3 | array | |
| rpm-color6 | `1F 00 FF 05` | 3 | array | |
| rpm-color7 | `1F 00 FF 06` | 3 | array | |
| rpm-color8 | `1F 00 FF 07` | 3 | array | |
| rpm-color9 | `1F 00 FF 08` | 3 | array | |
| rpm-color10 | `1F 00 FF 09` | 3 | array | |
| button-color1 | `1F 01 FF 00` | 3 | array | |
| button-color2 | `1F 01 FF 01` | 3 | array | |
| button-color3 | `1F 01 FF 02` | 3 | array | |
| button-color4 | `1F 01 FF 03` | 3 | array | |
| button-color5 | `1F 01 FF 04` | 3 | array | |
| button-color6 | `1F 01 FF 05` | 3 | array | |
| button-color7 | `1F 01 FF 06` | 3 | array | |
| button-color8 | `1F 01 FF 07` | 3 | array | |
| button-color9 | `1F 01 FF 08` | 3 | array | |
| button-color10 | `1F 01 FF 09` | 3 | array | |
| button-color11 | `1F 01 FF 0A` | 3 | array | |
| button-color12 | `1F 01 FF 0B` | 3 | array | |
| button-color13 | `1F 01 FF 0C` | 3 | array | |
| button-color14 | `1F 01 FF 0D` | 3 | array | |
| flag-color1 | `15 02 00` | 3 | array | |
| flag-color2 | `15 02 01` | 3 | array | |
| flag-color3 | `15 02 02` | 3 | array | |
| flag-color4 | `15 02 03` | 3 | array | |
| flag-color5 | `15 02 04` | 3 | array | |
| flag-color6 | `15 02 05` | 3 | array | |
| rpm-brightness | `1B 00 FF` | 1 | int | |
| buttons-brightness | `1B 01 FF` | 1 | int | |
| flags-brightness | `1B 02 FF` | 1 | int | |
| paddles-calibration | `08` | 1 | int | Write-only |

### Group `0x3F` (63) — Live Telemetry (write-only)

These use the same write group as configuration above. They send real-time data to the wheel's LED bar and button LEDs.

See [moza-protocol.md § LED color commands](moza-protocol.md#led-color-commands) for the LED encoding (index, R, G, B per LED, 5 per 20-byte chunk). Use index `0xFF` for unused padding slots to prevent firmware from overwriting LED 0.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-rpm-telemetry | `1A 00` | 2 | array | Current RPM position on the LED bar; see [moza-protocol.md § RPM LED telemetry](moza-protocol.md#rpm-led-telemetry-group-0x3f-device-0x17-cmd-0x1a-0x00) |
| send-buttons-telemetry | `1A 01` | 2 | array | |
| telemetry-rpm-colors | `19 00` | 20 | array | 5 LEDs per chunk; 2 chunks needed for 10 RPM LEDs |
| telemetry-button-colors | `19 01` | 20 | array | 3 chunks for 14 button LEDs; pad unused entries with index `0xFF` |

### Group `0x41` (65) — Telemetry Enable (write-only)

Confirmed in USB capture: sent to device `0x17` at ~100×/sec with payload always `00 00 00 00`. Likely a mode/enable flag. See [moza-protocol.md § dash telemetry enable](moza-protocol.md#dash-telemetry-enable-group-0x41-device-0x17-cmd-0xfd-0xde).

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-telemetry | `FD DE` | 4 | int | Wheels with integrated display; always `00 00 00 00` in captures |
| old-send-telemetry | `FD DE` | 4 | int | Old wheel firmware without integrated display |

### Group `0x43` (67) — Live Telemetry Stream (write-only)

Main game telemetry sent at ~17–20×/sec. See [moza-protocol.md § live telemetry stream](moza-protocol.md#live-telemetry-stream-group-0x43-device-0x17-cmd-0x7d-0x23) for full packet analysis and bit-packing format.

Payload = 2-byte cmd ID + 6-byte header + variable-length bit-packed channel data. Header bytes 0–3 are constant (`32 00 23 32`), byte 4 is a flag/stream selector, byte 5 is constant (`0x20`). Three concurrent streams use consecutive flag values for `package_level` tiers 30/500/2000. Channel data is bit-packed alphabetically by URL suffix per the active dashboard; payload size = `ceil(total_channel_bits / 8)`. Empty tiers send a 2-byte stub.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| send-live-telemetry | `7D 23` | varies | array | 6-byte header + bit-packed channel data; size depends on dashboard |
| send-telemetry-state | `FC 00` | 3 | array | Session acknowledgment (`session + ack_seq`) ~1×/sec |
| dashboard-transfer | `7C 00` | varies | array | Session-based chunked file transfer / RPC; see [moza-protocol.md § dashboard upload](moza-protocol.md#dashboard-upload-protocol) |
| display-config | `7C 27` | 4–8 | array | Periodic display config push (~1/s), page-cycled alongside `7C 23` |
| dashboard-activate | `7C 23` | 8 | array | Periodic dashboard activate (~1/s), interleaved per page with `7C 27`; declares active pages |
| display-settings | `7C 1E` | 8 | array | Periodic display settings push (~1/s) — brightness/timeout/orientation; sent to all wheel models |

### Old-Protocol Commands (Groups `0x3F` / `0x40`)

Used by older wheel firmware revisions. Observed in protocol captures and retained for backwards compatibility.

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-indicator-mode | `04` | 1 | int | 1=RPM, 2=Off, 3=On (1-based) |
| old-rpm-color1 | `15 00 00` | 3 | array | |
| old-rpm-color2 | `15 00 01` | 3 | array | |
| old-rpm-color3 | `15 00 02` | 3 | array | |
| old-rpm-color4 | `15 00 03` | 3 | array | |
| old-rpm-color5 | `15 00 04` | 3 | array | |
| old-rpm-color6 | `15 00 05` | 3 | array | |
| old-rpm-color7 | `15 00 06` | 3 | array | |
| old-rpm-color8 | `15 00 07` | 3 | array | |
| old-rpm-color9 | `15 00 08` | 3 | array | |
| old-rpm-color10 | `15 00 09` | 3 | array | |
| old-rpm-brightness | `14 00` | 1 | int | |

### Extended LED Group Architecture (Groups `0x3F` / `0x40`)

Newer wheels organize LEDs into 5 independently controlled groups, extending beyond the RPM (Shift) and Button groups above. Found in rs21_parameter.db. See [moza-protocol.md § wheel LED group architecture](moza-protocol.md#wheel-led-group-architecture-groups-0x3f0x40--6364-extended).

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |

Per-group commands (G = group ID 0–4, N = LED index):

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| group-brightness | `1B [G] FF` | 1 | int | Plugin command `wheel-group{G}-brightness` (G=2..4). Firmware answers even when hardware absent — cannot be used as a presence check |
| group-normal-mode | `1C [G]` | 1 | int | Telemetry-active mode. Plugin command `wheel-group{G}-mode` |
| group-standby-mode | `1D [G]` | 1 | int | Idle mode. Not yet exposed by plugin |
| group-standby-interval | `1E [G] [2..6]` | 2 | int | 2=breath, 3=circular, 4=rainbow, 5=drift sand, 6=breath color. Not yet exposed by plugin |
| group-led-color | `1F [G] FF [N]` | 3 | array | LED N static RGB. Plugin commands `wheel-rpm-color{1..25}` (G=0), `wheel-button-color{1..16}` (G=1), `wheel-group{G}-color{1..Nmax}` (G=2..4) |
| group-live-colors | `19 [G]` | 20 | array | Bulk live telemetry frame (packed `[idx, R, G, B]` entries, 0xFF padding). **Only groups 0/1 confirmed** — 2/3/4 may or may not support. Plugin `wheel-telemetry-rpm-colors`, `wheel-telemetry-button-colors` |
| group-live-bitmask | `1A [G]` | 2 | int | Per-frame active-LED bitmask (LE). Groups 0/1 only. Plugin `wheel-send-rpm-telemetry`, `wheel-send-buttons-telemetry` |

**Static vs live paths**: groups 0/1 have two rendering pipelines. Static (`1F`) writes persist in EEPROM and render only when firmware is in idle/constant mode (`wheel-telemetry-mode=2`, `wheel-buttons-idle-effect=1`). Live (`19` + `1A`) writes a volatile frame buffer used while telemetry is active. Groups 2-4 have only the static path in documented commands.

Additional newer wheel commands:

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| meter-auto-rotation | `10` | 1 | int | |
| sleep-mode | `20` | 1 | int | |
| sleep-timeout | `21` | 2 | int | |
| sleep-breath-interval | `22 01` | 2 | int | |
| sleep-breath-brightness | `23 [0/1]` | 1 | int | min (0) / max (1) |
| sleep-breath-color | `24 FF 01 FF` | 3 | array | RGB |
| startup-color | `25` | 3 | array | RGB |
| paddle-thresholds | `26` | 24 | array | 12× 2-byte thresholds |
| rotary-switch-color | `27 [N] [0/1]` | 3 | array | Switch N (0–4) foreground/background RGB |
| multi-function-switch | `28 [0..2]` | 1 | int | Enable, count, left/right assignment |
| rotary-signal-mode | `2A [N]` | 1 | int | Encoder N (0–4) signal mode |

---

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

---

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

---

## E-Stop (Device `0x1C` / 28)

| Command | Read Group | ID | Bytes | Type | Notes |
|---------|------------|----|-------|------|-------|
| receive-status | `0xC6` (198) | `00` | 1 | int | |
| get-status | `0x46` (70) | `01` | 1 | int | |

---

## Hub (Device `0x12` / 18)

### Group `0x64` (100) — Connected Device Status (read-only)

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| base | `02` | 2 | int | |
| port1 | `03` | 2 | int | |
| port2 | `04` | 2 | int | |
| port3 | `05 01` | 1 | int | |
| pedals1 | `06` | 2 | int | |
| pedals2 | `07` | 2 | int | |
| pedals3 | `08` | 2 | int | |
