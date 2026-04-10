# Moza Racing serial protocol

## Frame format

```
7E  [N]  [group]  [device]  [payload: N bytes]  [checksum]
```

| Field | Size | Description |
|-------|------|-------------|
| Start | 1 | Always `0x7E` |
| N | 1 | Byte count of payload only (excludes group, device, checksum) |
| Group | 1 | Request group / command category |
| Device | 1 | Target device ID on the internal serial bus |
| Payload | N | Command ID (1+ bytes) followed by value bytes |
| Checksum | 1 | See below |

Command IDs that are arrays of integers must be provided sequentially in order. Values are big-endian. Multiple frames can be concatenated in a single USB bulk transfer.

### Checksum

`checksum = (0x0D + sum of all preceding bytes including 0x7E) % 256`

The magic value 13 (`0x0D`) incorporates the USB endpoint (`0x02`), transfer type (`0x03` for URB_BULK), and a length constant (`0x08`). Changing the magic value causes devices to not respond — likely a firmware quirk rather than intentional.

### Responses

| Field | Transform |
|-------|-----------|
| Group | Request group + `0x80` (MSB set) — e.g. request `0x21` → response `0xA1` |
| Device | Nibbles swapped — e.g. request `0x13` → response `0x31` |
| Payload length | Reflects response data size, not request size |

Write requests: response mirrors the request payload. Read requests: response contains the full stored value regardless of how many bytes the request sent (a 1-byte read probe returns a full 16-byte string).

### Command chaining

Multiple commands can be sent at once. Responses are **not guaranteed in request order** — match by group number.

---

## USB topology

Device: Moza composite USB device (VID `0x346E` PID `0x0006`).

| Interface | Type | Endpoints | Purpose |
|-----------|------|-----------|---------|
| MI_00 | USB serial (CDC) | 0x02 OUT / 0x82 IN | Moza protocol bus — all serial frames |
| MI_02 | HID | 0x03 OUT / 0x83 IN | Wheel axes/buttons (not telemetry) |

Device IDs (19=base, 20=dash, 23=wheel, etc.) are addresses on the internal serial bus routed through the wheelbase hub — not separate USB devices.

**All captured live telemetry is addressed to device 0x17 (wheel, ID 23).** No captures exist of telemetry being sent to device 0x14 (MDD / standalone dash).

---

## Device and command reference

See [serial.md](serial.md) for the full list of device IDs and commands.

### Authoritative source: rs21_parameter.db

The Pit House installation contains `bin/rs21_parameter.db` — a SQLite database with 919 commands across 23 groups. This is the canonical reference for all RS21 (sim racing) device commands, including command names, descriptions, request/response group encoding, payload sizes, data types, valid ranges, and EEPROM addresses. The `request_group` field encodes as a JSON array: first element is the protocol group byte, remaining elements are command ID bytes. Example: `[40, 2]` → group 0x28, cmd 0x02. See [pithouse-re.md § rs21_parameter.db](pithouse-re.md#rs21_parameterdb--authoritative-command-database) for full schema and analysis.

Commands NOT in the database (not in rs21_parameter.db; discovered via USB captures): identity queries (groups 7/8/15/16), music sub-commands (group 42), sequence counter (group 45), telemetry enable (group 65), and live telemetry stream (group 67/0x43).

---

## Heartbeat (group 0x00)

Sent to every known device ID (18–30) roughly once per second. Payload length 0. Purpose: keep-alive / presence check.

## Unsolicited messages

- **Group 0x0E** from wheel (device 23): ASCII debug/log text, ~every 2s. Contains NRF radio stats, e.g. `NRFloss[avg:0.00000%] recvGap[avg:4.70100ms]`.
- **Group 0x06** from wheel (device 23): emitted on connection, ~12 bytes. Possibly a partial hardware identifier.

---

## Wheel connection probe sequence

When a wheel is detected, Pithouse queries device 0x17 for identity. All identity strings are 16-byte null-padded ASCII.

| Group | Cmd ID | Response | Notes |
|-------|--------|----------|-------|
| 0x02 | — | 1 byte (e.g. `0x02`) | Possibly protocol version |
| 0x04 | `0x00` + 3 zero bytes | 2 bytes | Unknown |
| 0x05 | `0x00` + 3 zero bytes | 4 bytes, per-model | Capability flags? VGS: `01 02 1f 01`; CS V2.1: `01 02 26 00` |
| 0x07 | `0x01` | 16-byte string | **Model name** — `VGS`, `CS V2.1`, `R5 Black # MOT-1` |
| 0x08 | `0x01` | 16-byte string | **HW version** — `RS21-W08-HW SM-C` |
| 0x08 | `0x02` | 16-byte string | **HW revision** — `U-V12`, `U-V02` |
| 0x0F | `0x01` | 16-byte string | **FW version** — `RS21-W08-MC SW` |
| 0x10 | `0x00` | 16-byte string | **Serial number, first half** |
| 0x10 | `0x01` | 16-byte string | **Serial number, second half** |
| 0x11 | `0x04` | 2 bytes | Unknown |

Full serial = two halves concatenated (32 ASCII chars).

---

## LED color commands

RPM and button LED colors use `wheel-telemetry-rpm-colors` and `wheel-telemetry-button-colors`. Fixed payload size of 20 bytes per chunk; colors split across multiple writes.

Each LED: 4 bytes `[index, R, G, B]`. Five LEDs per chunk (5 × 4 = 20). With 10 RPM LEDs = 2 chunks. With 14 button LEDs = 3 chunks (last padded to 20 bytes).

**Padding:** use index `0xFF` for unused entries, not `0x00`. Zero-padding creates `[0x00, 0x00, 0x00, 0x00]` which the firmware interprets as "set LED 0 to black", causing flicker.

---

## RPM LED telemetry (group 0x3F, device 0x17, cmd `[0x1A, 0x00]`)

Sent ~once per second to the wheel. 8 data bytes = 4 × 16-bit LE values:

```
[current_pos, 0x0000, 0x03FF, 0x0000]
```

- `current_pos` = `current_rpm / max_rpm × 1023` — 10-bit RPM fraction
- Value 3 is always 1023 (fixed denominator)
- Values 2 and 4 are always 0

---

## Dash telemetry enable (group 0x41, device 0x17, cmd `[0xFD, 0xDE]`)

Sent ~100×/s. Data is always `00 00 00 00`. Likely a mode/enable flag — value 0 = telemetry active.

---

## Main real-time telemetry (group 0x43, device 0x17, cmd `[0x7D, 0x23]`)

Primary live data stream from Pithouse to wheel/dash. Sent ~17–20×/s.

### Frame structure

```
7E [N] 43 17  7D 23  [6-byte header]  [live data]  [checksum]
```

**Header** (6 bytes, after cmd ID):

| Byte | Value | Notes |
|------|-------|-------|
| 0–3 | `32 00 23 32` | Constant across all captures |
| 4 | varies | **Flag byte** — determines payload type (see below) |
| 5 | `0x20` | Constant across all captures |

### Flag byte and payload types

Multiple flag values appear within a single session. The majority flag (>95% of packets) carries the main telemetry; minority flags carry different data with different byte counts.

| Majority flag | Minority flags | Live bytes | Content |
|--------------|----------------|------------|---------|
| `0x13` or `0x0a` (varies per session) | — | 16 | Main telemetry (bit-packed channels) |
| — | `0x14` / `0x0b` | 2 | Unknown (e.g. `9a:01`) |
| — | `0x15` / `0x0c` | 13 | Unknown (e.g. `00:00:00:00:00:00:00:00:dd:77:df:7d:f7`) |

**Only majority-flag packets should be decoded as main telemetry.**

### Payload size is dashboard-specific

Pithouse packs channels **alphabetically by URL suffix** (e.g. `BestLapTime` before `Brake` before `CurrentLapTime`). The packing loop adds channels in this order until a per-dashboard byte limit is reached; remaining channels are **dropped**.

Total payload bytes = `ceil(sum_of_channel_bit_widths / 8)`.

| Dashboard | Channels in mzdash | Live data bytes | Notes |
|-----------|--------------------|-----------------|-------|
| `m Formula 1.mzdash` | 16 | 16 | 8 channels fit (see verified layout below) |
| Core (default) | 6 | varies | Channels sorted alphabetically, packed until limit |
| `rpm-only.mzdash` | 1 | 2 | Single uint16_t RPM value |

With 1 channel: 2 live bytes = **LE uint16 raw RPM value**.
Multi-channel payloads are **bit-packed** using per-channel compression types.

### Bit-packed encoding

Pithouse's `Telemetry.json` assigns each telemetry channel a `compression` type, which determines the bit width and encoding used in the telemetry stream.

#### Verified bit widths

The following bit widths are verified from USB capture analysis and Telemetry.json:

| Compression | Bits | Interface class | Value encoding |
|-------------|------|-----------------|----------------|
| `bool` | 1 | `BoolInterface` | 0 or 1 |
| `int30` | 5 | `Int30Interface` | Signed, values -1 to 30 (Gear uses offset: -1=R, 0=N, 1-12) |
| `uint30` / `uint31` | 5 | `Int30Interface` | Same 5-bit field, unsigned interpretation |
| `uint15` | 4 | `Int15Interface` | 0-14 valid; 15 = not available |
| `int8_t` / `uint8_t` | 8 | `Int8Interface` | Signed/unsigned byte |
| `float_001` | 10 | `NormalizedInterface` | 0-1000 valid, 1023 = N/A; encode: `raw = game × 1000`; decode: `game = raw / 1000` |
| `percent_1` | 10 | `PercentInterface` | 0-1000 valid, 1023 = N/A; encode: `raw = game% × 10`; decode: `game% = raw / 10` |
| `tyre_pressure_1` | 12 | `TyrePressureInterface` | encode: `raw = kPa × 10`; decode: `kPa = raw × 0.1`; range 0–409.5 kPa |
| `tyre_temp_1` / `track_temp_1` / `oil_pressure_1` | 14 | `TyreTempInterface` | encode: `raw = °C × 10 + 5000`; decode: `°C = (raw − 5000) × 0.1`; range −500–1138.3°C |
| `uint16_t` / `int16_t` | 16 | `Int16Interface` | Raw 16-bit integer |
| `float_6000_1` | 16 | `UFloatInterface` | encode: `raw = game × 10`; decode: `game = raw / 10`; range 0–6553.5 |
| `float_600_2` | 16 | `UFloatInterface` | encode: `raw = game × 100`; decode: `game = raw / 100`; range 0–655.35 |
| `brake_temp_1` | 16 | `BrakeTempInterface` | encode: `raw = °C × 10 + 5000`; decode: `°C = (raw − 5000) / 10`; range −500–6053.5°C |
| `uint24_t` | 24 | `UInt24Interface` | Raw 24-bit integer |
| `float` | 32 | `DoubleInterface` (flag=1) | Raw IEEE 754 single-precision float |
| `int32_t` / `uint32_t` | 32 | `Int32Interface` | Raw 32-bit integer |
| `double` / `int64_t` / `uint64_t` / `location_t` | 64 | `DoubleInterface` (flag=0) / `Int64Interface` | Raw 64-bit value |
| `uint3` | 4 | `Int15Interface` | 0-14 valid, 15 = N/A (same as `uint15`) |
| `uint8` | 4 | `Int15Interface` | 0-14 valid, 15 = N/A |

`DoubleInterface` has a flag byte at object offset +4: flag=1 returns 32-bit (used for `float` compression), flag=0 returns 64-bit (used for `double`).

Factory ID 20 (`uint3`, `uint8`, `uint15`) maps through abstract `IsUnsignedInterface` to `Int15Interface` (4 bits). The type name's number does NOT determine the bit width — all three use 4 bits.

#### Channel ordering

Channels are packed **alphabetically by URL suffix** (the part after `v1/gameData/`). Internally, `TelemetryServer::updateTelemetry` iterates a `std::map<QString, ...>` keyed by channel URL, which yields channels in sorted order. The `TelemetryBitFormat::assemble` function packs them sequentially into the bit stream starting at bit 0.

Bits are packed **LSB-first within each byte** (bit 0 = LSB of byte 0, bit 8 = LSB of byte 1, etc.). Multi-bit fields span byte boundaries when needed.

#### F1 dashboard bit layout

Two versions of the F1 dashboard exist in different Pit House installs. Both pack 8 channels into 16 bytes.

**`m Formula 1.mzdash`** (older, used in USB captures):

Channels sorted alphabetically by URL suffix (F < G-A < G-e): FuelRemainder comes before GAP which comes before Gear.

| Bits | Channel | URL suffix | Compression | Width | Notes |
|------|---------|------------|-------------|-------|-------|
| 0–31 | BestLapTime | `BestLapTime` | `float` | 32 | IEEE float; 0 before first lap |
| 32–41 | Brake | `Brake` | `float_001` | 10 | ~26% at rest, up to 87% braking |
| 42–73 | CurrentLapTime | `CurrentLapTime` | `float` | 32 | IEEE float; monotonic within laps |
| 74 | DrsState | `DrsState` | `bool` | 1 | 0 in AC practice |
| 75–78 | ErsState | `ErsState` | `uint3` | 4 | 0 in AC practice |
| 79–88 | FuelRemainder | `FuelRemainder` | `percent_1` | 10 | Fuel % × 10 |
| 89–120 | GAP | `GAP` | `float` | 32 | Delta to session best; 0 in practice |
| 121–125 | Gear | `Gear` | `int30` | 5 | {0,1,2,3,4,5,6} = N + gears 1–6 |
| 126–127 | *(padding)* | | | 2 | |

Dropped: LastLapTime, Rpm, SpeedKmh, Throttle, TyreWear×4 (all alphabetically after Gear, exceed 16-byte limit after adding Gear).

**`Formula 1.mzdash`** (current Pit House, no `m ` prefix):

Has 15 channels (no FuelRemainder; adds Rpm, SpeedKmh, Throttle, TyreWear×4). With 16-byte inclusive limit, 8 channels pack to exactly 16 bytes:

| Bits | Channel | URL suffix | Compression | Width |
|------|---------|------------|-------------|-------|
| 0–31 | BestLapTime | `BestLapTime` | `float` | 32 |
| 32–41 | Brake | `Brake` | `float_001` | 10 |
| 42–73 | CurrentLapTime | `CurrentLapTime` | `float` | 32 |
| 74 | DrsState | `DrsState` | `bool` | 1 |
| 75–78 | ErsState | `ErsState` | `uint3` | 4 |
| 79–110 | GAP | `GAP` | `float` | 32 |
| 111–115 | Gear | `Gear` | `int30` | 5 |
| 116–125 | Throttle | `Throttle` | `float_001` | 10 |
| 126–127 | *(padding)* | | | 2 |

Dropped: LastLapTime, Rpm, SpeedKmh, TyreWear×4 (would each push total above 16 bytes).

---

## Telemetry startup sequence (from capture analysis)

Pit House sends several concurrent command streams when telemetry is active. Analysis of the `dash.ndjson` capture (which includes the pre-telemetry phase) shows the startup order:

### Concurrent outbound streams during active telemetry

| Stream | Rate | Device | Group/Cmd | Purpose |
|--------|------|--------|-----------|---------|
| Sequence counter | ~45/s | base (0x13) | `0x2D/F5:31` | Frame sync to base |
| Telemetry enable | ~42/s | wheel (0x17) | `0x41/FD:DE` data=`00:00:00:00` | Mode/enable flag |
| **Live telemetry** | ~31/s | wheel (0x17) | `0x43/7D:23` | Bit-packed game data |
| Heartbeat | ~8/s | all devices | `0x00` n=0 | Keep-alive |
| RPM LED position | ~4/s | wheel (0x17) | `0x3F/1A:00` | LED bar position |
| Telemetry mode | ~3/s | wheel (0x17) | `0x40/28:02` data=`01:00` | Set/poll multi-channel mode |
| Dash heartbeat | ~1.5/s | dash/dev21 | `0x43` n=1 | Dash keep-alive |
| Settings block | ~1/s | wheel (0x17) | `0x43/7C:00` | Config sync |
| Status push | ~1/s | wheel (0x17) | `0x43/FC:00` | Status push |
| Button LED | ~1/s | wheel (0x17) | `0x3F/1A:01` | Button LED state |

### Startup timeline

From `dash.ndjson` (capture starts before telemetry is active):

| Time | Command | Notes |
|------|---------|-------|
| t=0.015 | `0x2D/F5:31` | Sequence counter starts immediately |
| t=0.022 | `0x41/FD:DE` | Enable signal starts (runs continuously at ~50/s) |
| t=0.032 | `0x00` heartbeat | First heartbeat to wheel |
| t=0.135 | `0x3F/1A:00` | First RPM LED update |
| t=0.150 | `0x00` heartbeat burst | Heartbeat to ALL devices |
| t=0.244 | `0x40/28:02 data=01:00` | Set telemetry mode to multi-channel |
| t=0.591 | `0x43/FC:00` | Status push |
| **t=0.672** | **`0x43/7D:23`** | **First live telemetry frame** |

The enable signal (`0x41`) runs from t=0.022 — **0.65 seconds before** the first telemetry frame. The telemetry mode set (`0x40/28:02 data=01:00`) runs from t=0.244.

### Minimum viable sender (hypothesis)

Once a dashboard has been configured on the wheel by Pit House, the wheel is pre-configured to listen for telemetry with a specific channel layout. The RPM LEDs work the same way — the wheel just processes whatever `0x3F/1A:00` data arrives with zero preamble. By analogy, **the minimum viable telemetry sender is likely just the `0x43/7D:23` frames themselves**.

The `dash.ndjson` capture was taken during an AC pause, so the 0.65s gap before telemetry frames appear is Pit House not sending data while paused, not a startup sequence. The `0x41/FD:DE` enable signal and `0x40/28:02` mode polling are likely Pit House housekeeping (internal state management, UI sync) rather than wheel firmware requirements.

**Recommended approach:** Start with just `0x43/7D:23` frames at ~20-30 Hz. If the wheel doesn't respond, add the enable signal (`0x41`). If still nothing, add the telemetry mode set (`0x40/28:02 data=01:00`).

**Note:** Requires user testing to confirm. The critical prerequisite is that Pit House has already uploaded a dashboard to the wheel — this configures the channel layout the wheel expects to decode.

---

## Other periodic commands

### Group 0x40 (host → device 0x17)

**Normal operation (~3.4 Hz):** `28 02 XX 00` — byte 2 varies by dashboard type (`01` with 16-channel, `00` with 1-channel).

**Dashboard upload:** burst of 18+ distinct payloads including channel enable/disable (`1E [0/1] [ch]`), page config (`1B [page] FF`), and various sub-commands.

### Group 0x2D (host → device 0x13, ~50 Hz)

Cmd `[0xF5, 0x31]`. Data: `00 00 00 XX` where XX increments by 1 each send. Sequence counter for the base unit.

### Group 0x0E poll (host → device 0x13, ~1 Hz)

3-byte payload `00 01 XX` with 16-bit BE countdown counter starting at 0x013A (314). Base echoes back + 4 unknown bytes.

### Group 0x1F (host → device 0x12, ~3 Hz)

`4F XX 00/01` where XX cycles `08`→`09`→`0A`→`0B`. Response inserts `0xFF` status byte.

### Group 0x2B (host → device 0x13, occasional)

`02 00 00`, sent on state changes (pause, session end).

### Group 0x43 sub-commands (device 0x17)

| Cmd ID | Data | Frequency | Notes |
|--------|------|-----------|-------|
| `[0xFC, 0x00]` | 3 bytes | ~once per 5s | Periodic status/config write |
| `[0x7C, 0x00]` | 25 bytes | ~once per 60s | Possibly settings block |

### Group 0x43 broadcast (devices 0x14, 0x15)

Short (length=2) packets to dash (device 20) and device 21 every ~5s. Heartbeat/sync.

---

## Telemetry encode/decode formulas

See [pithouse-re.md § Value encoding details](pithouse-re.md#9-value-encoding-details) for full details.

### Complete encode reference (game value → raw bits)

| Compression | Bits | Encode | Decode | Range |
|-------------|------|--------|--------|-------|
| `bool` | 1 | `raw = value` | `value = raw` | 0–1 |
| `uint3` / `uint8` / `uint15` | 4 | `raw = min(value, 15)` | `value = raw` (15 = N/A) | 0–14 |
| `int30` / `uint30` / `uint31` | 5 | `raw = min(value, 31)` | `value = raw` | 0–31 |
| `int8_t` / `uint8_t` | 8 | `raw = value` | `value = raw` | 0–255 |
| `percent_1` | 10 | `raw = clamp(game% × 10, 0, 1000)` | `game% = raw / 10` | 0–100%, 1023=N/A |
| `float_001` | 10 | `raw = clamp(game × 1000, 0, 1000)` | `game = raw / 1000` | 0.0–1.0, 1023=N/A |
| `tyre_pressure_1` | 12 | `raw = clamp(kPa × 10, 0, 4095)` | `kPa = raw × 0.1` | 0–409.5 kPa |
| `tyre_temp_1` / `track_temp_1` / `oil_pressure_1` | 14 | `raw = °C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C |
| `int16_t` / `uint16_t` | 16 | `raw = value` | `value = raw` | 0–65535 |
| `float_6000_1` | 16 | `raw = clamp(game × 10, 0, 65535)` | `game = raw / 10` | 0–6553.5 |
| `float_600_2` | 16 | `raw = clamp(game × 100, 0, 65535)` | `game = raw / 100` | 0–655.35 |
| `brake_temp_1` | 16 | `raw = clamp(°C × 10 + 5000, 0, 65535)` | `°C = (raw − 5000) / 10` | −500–6053.5°C |
| `uint24_t` | 24 | `raw = value` | `value = raw` | 0–16777215 |
| `float` | 32 | `raw = IEEE 754 single bits` | IEEE 754 reinterpret | full float range |
| `int32_t` / `uint32_t` | 32 | `raw = value` | `value = raw` | full 32-bit |
| `double` / `location_t` / `int64_t` / `uint64_t` | 64 | `raw = IEEE 754 double bits` | IEEE 754 reinterpret | full 64-bit |

### Key constants

| Value | Usage |
|-------|-------|
| 10.0 | Scale factor for percent, UFloat, temps, pressures (×10) |
| 100.0 | Normalized → percent conversion (×100 then ×10) |
| 1000.0 | Max raw for 10-bit percent/normalized |
| 5000.0 | Temperature offset (raw = temp×10 + 5000) |
| 65535.0 | Max raw for 16-bit UFloat/BrakeTemp |
| 409.5 | TyrePressure max (kPa) |
| 1138.3 | TyreTemp max (°C) |
| −500.0 | TyreTemp min (°C) |

### UFloatInterface scale factor

UFloatInterface reads a per-instance exponent from `this+8`. The scale factor is `10^exponent`:
- `float_6000_1`: exponent=1 → scale=10 → range 0–6553.5
- `float_600_2`: exponent=2 → scale=100 → range 0–655.35

The type name encodes `float_{max}_{decimal_places}`: `float_6000_1` means max ~6000 with 1 decimal place.

---

## ServiceParameter value transforms (rs21_parameter.db)

The `ServiceParameter` table in `rs21_parameter.db` documents how raw **device setting** values (groups 31–100) map to display units. These are separate from the telemetry encoding above — they apply to Pit House's settings UI, NOT to the telemetry bit stream.

| Function | Params | Example | Meaning |
|----------|--------|---------|---------|
| `multiply` | `0.01` | FFB strength 0–10000 → 0–100% | Raw value × 0.01 |
| `multiply` | `0.1` | Temperature raw → degrees | Raw value × 0.1 |
| `multiply` | `0.05` | Step values | Raw value × 0.05 |
| `multiply` | `2` | Some parameters | Raw value × 2 |
| `division` | `65535` | Normalize 16-bit | Raw value / 65535 → 0.0–1.0 |
| `division` | `16384` | Normalize 14-bit | Raw value / 16384 → 0.0–1.0 |
| `softLimitStiffness_conversion` | — | Soft limit stiffness | Custom non-linear conversion |

---

## EEPROM direct access (group 0x0A / 10)

Low-level EEPROM read/write protocol, sending to any device. Bypasses the named command interface. Found in rs21_parameter.db but not observed in USB captures.

| Cmd ID | Bytes | Dir | Purpose |
|--------|-------|-----|---------|
| `[00, 05]` | 4 | W | Select EEPROM table ID |
| `[00, 06]` | 4 | R | Read selected table ID |
| `[00, 07]` | 4 | W | Select address within table |
| `[00, 08]` | 4 | R | Read selected address |
| `[00, 09]` | 4 | W | Write int at selected table+address |
| `[00, 0A]` | 4 | R | Read int at selected table+address |
| `[00, 0B]` | 4 | W | Write float at selected table+address |
| `[00, 0C]` | 4 | R | Read float at selected table+address |

EEPROM tables: 2=Base (38 params), 3=Motor (76 params, PID/encoder/field-weakening), 4=Wheel (123 params), 5=Pedals (45 params), 11=Unknown (8 params). See [pithouse-re.md § EEPROM tables](pithouse-re.md#eeprom-tables) for full parameter listing.

---

## Base ambient LED control (groups 0x20/0x22 — 32/34)

Controls 2 LED strips (9 LEDs each) on the wheelbase body. Group 32 = write, group 34 = read. Sent to the main device (0x12). Found in rs21_parameter.db but not observed in USB captures.

| Cmd ID | Bytes | Purpose |
|--------|-------|---------|
| `[1C]` | 1 | Indicator group state (on/off) |
| `[1D]` | 1 | Standby mode (0=constant, 2=breath, 3=cycle, 4=rainbow, 5=flow) |
| `[1E, mode]` | 2 | Standby interval for mode |
| `[1F, 02]` | 1 | Brightness level |
| `[20, strip, mode, led]` | 3 | Per-LED RGB color. strip=0/1, mode=1(constant)/2(breath), led=0–8 |
| `[21]` | 1 | Sleep mode enable |
| `[22]` | 2 | Sleep timeout |
| `[23, 01]` | 2 | Sleep breathing interval |
| `[24]` | 1 | Sleep brightness |
| `[25, strip, 1, led]` | 3 | Sleep breathing per-LED RGB color |
| `[26]` | 3 | Startup LED color (RGB) |
| `[27]` | 3 | Shutdown LED color (RGB) |

---

## Wheel LED group architecture (groups 0x3F/0x40 — 63/64, extended)

The rs21_parameter.db reveals that newer wheels organize LEDs into **5 independently controlled groups**, extending beyond the RPM/button/flag groups documented in serial.md.

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |

Per-group commands (G = group ID 0–4, N = LED index):

| Cmd ID | Bytes | Purpose |
|--------|-------|---------|
| `[1B, G, FF]` | 1 | Brightness |
| `[1C, G]` | 1 | Normal (telemetry active) mode |
| `[1D, G]` | 1 | Standby (idle) mode |
| `[1E, G, 2..6]` | 2 | Standby animation interval (2=breath, 3=circular, 4=rainbow, 5=drift sand, 6=breath color) |
| `[1F, G, FF, N]` | 3 | LED N static RGB color |

Additional new wheel commands:

| Cmd ID | Bytes | Purpose |
|--------|-------|---------|
| `[10]` | 1 | Meter auto-rotation |
| `[20]` | 1 | Sleep mode enable |
| `[21]` | 2 | Sleep timeout |
| `[22, 01]` | 2 | Sleep breath cycle interval |
| `[23, 0/1]` | 1 | Sleep breath brightness min/max |
| `[24, FF, 01, FF]` | 3 | Sleep breath RGB color |
| `[25]` | 3 | Startup RGB color |
| `[26]` | 24 | Paddle thresholds (12× 2-byte) |
| `[27, N, 0/1]` | 3 | Rotary switch N (0–4) foreground/background RGB |
| `[28, 0..2]` | 1 | Multi-function switch (enable, count, left/right assignment) |
| `[2A, N]` | 1 | Rotary encoder N (0–4) signal mode |

---

## Telemetry channel census (Telemetry.json)

The master channel list `bin/GameConfigs/Telemetry.json` defines 410 channels. Key distribution:

| Namespace | Count | Notes |
|-----------|-------|-------|
| `v1/gameData/` | 275 | Standard game telemetry |
| `v1/gameData/patch/` | 133 | Extended: 64 track map coordinates, 64 race info slots, display names |
| `v1/preset/` | 2 | Device state: `CurrentTorque` and `SteeringWheelAngle` (both `float_6000_1`, 16 bits) |

The `v1/preset/` channels are NOT game telemetry — they reflect the wheelbase's own state.

### Full compression type census

| Compression | Count | Bits | Encode | Primary use |
|-------------|-------|------|--------|-------------|
| `float` | 73 | 32 | IEEE 754 single | Lap times, delta, torque, fuel |
| `location_t` | 65 | 64 | IEEE 754 double | Track position coordinates |
| `uint32_t` | 65 | 32 | raw | Race info slots |
| `bool` | 51 | 1 | 0/1 | Flags, states, lights |
| `tyre_temp_1` | 43 | 14 | °C×10+5000 | Tyre temperatures |
| `percent_1` | 19 | 10 | %×10 | Throttle, brake, clutch, fuel, tyre wear |
| `string` | 15 | var | — | Player/track/game names |
| `brake_temp_1` | 14 | 16 | °C×10+5000 | Brake disc temperatures |
| `tyre_pressure_1` | 12 | 12 | kPa×10 | Tyre pressures |
| `float_600_2` | 12 | 16 | val×100 | Sector times |
| `uint8_t` | 12 | 8 | raw | Lap count, position |
| `uint8` | 5 | 4 | raw (max 15) | TC/ABS levels |
| `track_temp_1` | 5 | 14 | °C×10+5000 | Track/air/water temperatures |
| `float_6000_1` | 4 | 16 | val×10 | RPM-range values |
| `float_001` | 3 | 10 | val×1000 | Normalized 0–1 |
| `int32_t` | 3 | 32 | raw | Signed 32-bit |
| `uint16_t` | 2 | 16 | raw | MaxRpm, MaxSpeedKmh |
| `uint30` | 2 | 5 | raw (max 31) | Spotter car proximity |
| `int30` | 1 | 5 | raw (max 31) | Gear (0=N, 1-n=gears) |
| `uint15` | 1 | 4 | raw (max 15) | Boost |
| `uint31` | 1 | 5 | raw (max 31) | DRS allowed |
| `uint3` | 1 | 4 | raw (max 15) | ERS state |
| `oil_pressure_1` | 1 | 14 | °C×10+5000 | Oil pressure |

---

## Internal bus topology (monitor.json)

The `monitor.json` file in the Pit House installation defines the device tree for each base model. These are **internal bus IDs**, not the serial protocol device IDs. The mapping between bus IDs and protocol device IDs is: bus 2 → main (0x12), bus 3 → base (0x13), bus 4 → wheel (0x17), bus 5 → dash (0x14), etc.

Common topology (single-controller bases):
```
1 (USB host)
└── 2 (Main controller / hub)
    ├── 3 (Motor controller)
    ├── 4 (Wheel) ── 18 (Wheel display unit)
    ├── 5 (Dashboard) ── 17 (Dash sub-device)
    ├── 6..12 (Peripheral ports)
    ├── 13, 14 (children of 9)
    └── 16 (child of 7)
```

D11 (R21/R25/R27 Ultra) omits bus 5; S09 CM2 dash connects as bus 19 directly off bus 2.

See [pithouse-re.md § Device tree topology](pithouse-re.md#device-tree-topology-monitorjson) for full per-model trees and performance tuning parameters.

---

## Open questions

- ~~Value scaling for specialized types~~ — **RESOLVED**: All conversion formulas determined. See [pithouse-re.md § Value encoding details](pithouse-re.md#9-value-encoding-details). Key insight: the `percent_1` scale factor is exactly 10.0 (not 10.22 as previously estimated from capture data)
- **Dashboard byte limit configuration** — stored at config object offset `+0x30`, set during dashboard upload (group 0x40). Exact mechanism for setting this limit not yet traced
- **2-byte and 13-byte flag-variant payloads** — these use flag values base+1 and base+2, each a separate entry in the client connection map; content unknown
- ~~Flag byte origin~~ — **RESOLVED**: The flag is a Pit House-internal monotonic counter, incremented each time a new client connects (`readReady` command type 0x04). It is NOT communicated to the wheel during connection setup — `newConnectionRequest` does not send the flag value over serial. The flag serves only as a Pit House-side map key for client multiplexing. **The wheel firmware almost certainly does not validate the flag byte.** Any fixed value (e.g., `0x01`) should work for sending telemetry. See [pithouse-re.md § Flag byte](pithouse-re.md#5-flag-byte)
- **MDD (standalone dash)** — no captures of telemetry sent to device 0x14; protocol may differ
- **Gear encoding for reverse** — captures only show 0=N, 1-6=gears; reverse (-1) encoding not observed (may be stored as 31 in 5-bit unsigned, or with sign bit)
- **EEPROM direct access** — group 10 protocol found in rs21_parameter.db but never observed in USB captures; needs live verification
- **Base ambient LEDs** — groups 32/34 commands found in rs21_parameter.db; not captured in USB traces (requires base with LED strips)
- **Wheel LED groups 2-4** — Single, Rotary, and Ambient groups found in rs21_parameter.db with up to 56 LEDs; only groups 0 (Shift/RPM) and 1 (Button) confirmed in captures so far
