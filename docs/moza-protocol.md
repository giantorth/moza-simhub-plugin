# Moza Racing serial protocol

> **Disclaimer:** This document is a work in progress. It may contain errors, inconsistencies, omissions, or factually incorrect information. Research into the telemetry packet structure is ongoing.

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

The Pit House installation contains `bin/rs21_parameter.db` — a SQLite database with 919 commands across 23 groups. This is the canonical reference for all RS21 (sim racing) device commands, including command names, descriptions, request/response group encoding, payload sizes, data types, valid ranges, and EEPROM addresses. The `request_group` field encodes as a JSON array: first element is the protocol group byte, remaining elements are command ID bytes. Example: `[40, 2]` → group 0x28, cmd 0x02.

Commands NOT in the database (not in rs21_parameter.db; discovered via USB captures): identity queries (groups 7/8/15/16), music sub-commands (group 42), sequence counter (group 45), telemetry enable (group 65), and live telemetry stream (group 67/0x43).

---

## Heartbeat (group 0x00)

Sent to every known device ID (18–30) roughly once per second. Payload length 0. Purpose: keep-alive / presence check.

## Unsolicited messages

- **Group 0x0E** from wheel (device 23): ASCII debug/log text, ~every 2s. Contains NRF radio stats, e.g. `NRFloss[avg:0.00000%] recvGap[avg:4.70100ms]`.
- **Group 0x06** from wheel (device 23): 12-byte hardware identifier. In `connect-wheel-start-game.json` this is host-initiated (part of the probe sequence), not purely unsolicited. VGS response: `be 49 30 02 14 71 35 04 30 30 33 37`.

---

## Wheel connection probe sequence

When a wheel is detected, Pithouse queries device 0x17 for identity. All identity strings are 16-byte null-padded ASCII.

Observed probe order (from `connect-wheel-start-game.json`): 0x09, 0x04, 0x06, 0x02, 0x05, 0x07, 0x0F, 0x11, 0x08, 0x10.

| Group | Cmd ID | Response | Notes |
|-------|--------|----------|-------|
| 0x09 | — (n=0) | 2 bytes (e.g. `00 01`) | **Presence/ready check** — sent first, before all other probes. Response may indicate sub-device count |
| 0x02 | — | 1 byte (e.g. `0x02`) | Possibly protocol version |
| 0x04 | `0x00` + 3 zero bytes | 4 bytes, per-model | VGS: `01 02 04 06`; Display sub-device: `01 02 08 06`. Byte 2 may encode device type (0x04=wheel, 0x08=display) |
| 0x05 | `0x00` + 3 zero bytes | 4 bytes, per-model | Capability flags? VGS: `01 02 1f 01`; CS V2.1: `01 02 26 00`; Display: `01 02 00 00` |
| 0x06 | — (n=0) | 12 bytes | Hardware identifier. VGS: `be 49 30 02 14 71 35 04 30 30 33 37` |
| 0x07 | `0x01` | 16-byte string | **Model name** — `VGS`, `CS V2.1` (see [known model names](#known-wheel-model-names)) |
| 0x08 | `0x01` | 16-byte string | **HW version** — `RS21-W08-HW SM-C` |
| 0x08 | `0x02` | 16-byte string | **HW revision** — `U-V12`, `U-V02` |
| 0x0F | `0x01` | 16-byte string | **FW version** — `RS21-W08-MC SW` |
| 0x10 | `0x00` | 16-byte string | **Serial number, first half** |
| 0x10 | `0x01` | 16-byte string | **Serial number, second half** |
| 0x11 | `0x04` | 2 bytes | Unknown |

Full serial = two halves concatenated (32 ASCII chars).

### Display sub-device (inside VGS wheel)

During dashboard upload, Pithouse runs the same identity probe sequence against a **Display** sub-module inside the wheel (routed via `0x43` frames). The Display has a distinct identity:

| Field | VGS (wheel) | Display (sub-module) |
|-------|-------------|---------------------|
| Model (0x07) | `VGS` | `Display` |
| HW version (0x08/01) | `RS21-W08-HW SM-C` | `RS21-W08-HW SM-D` |
| HW revision (0x08/02) | `U-V12` | `U-V14` |
| Caps (0x05) | `01 02 1f 01` | `01 02 00 00` |
| Type (0x04) byte 2 | `04` | `08` |
| Serial | (differs) | (differs) |

The SM-C/SM-D suffix distinguishes the main controller from the display controller. The Display sub-device has no capability flags (`00 00` vs `1f 01`).

### Known wheel model names

Model names confirmed from USB captures and live serial queries:

| Model name | Wheel | Source |
|------------|-------|--------|
| `VGS` | Vision GS | USB capture (`cs-to-vgs-wheel.ndjson`). 8 button LEDs, no flag LEDs |
| `CS V2.1` | CS V2 | USB capture (`vgs-to-cs-wheel.ndjson`) |

Model names assumed from device naming conventions (unverified):

| Prefix | Wheel | Notes |
|--------|-------|-------|
| `GS V2P` | GS V2P | 10 button LEDs (5 per side), no flag LEDs |
| `CSP` | CS Pro | Has flag LEDs |
| `KSP` | KS Pro | Has flag LEDs |
| `KS` | KS | 10 button LEDs, no flag LEDs |
| `FSR2` | FSR V2 | Has flag LEDs |
| `TSW` | TSW | 14 button LEDs, no flag LEDs |

### ES wheel identity caveat

ES (old-protocol) wheels share device ID `0x13` with the wheelbase. Identity queries (group `0x07` etc.) sent to `0x13` return the **base** identity, not the wheel identity. For example, an ES wheel on an R5 base returns model name `R5 Black # MOT-1` — this is the base, not the wheel. There is currently no known way to query the ES wheel's own model name through the serial protocol.

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

### Flag byte, payload types, and multi-stream architecture

Pit House sends telemetry as **three concurrent streams** using different flag bytes, one per `package_level` tier defined in `GameConfigs/Telemetry.json`. Each stream carries the channels assigned to its tier, bit-packed alphabetically by URL suffix.

| Flag offset | `package_level` | Update rate | Content |
|-------------|----------------|-------------|---------|
| base (e.g. `0x0a`, `0x13`) | 30 | ~30 ms | Channels with `package_level: 30` |
| base+1 | 500 | ~500 ms | Channels with `package_level: 500` |
| base+2 | 2000 | ~2000 ms | Channels with `package_level: 2000` |

`package_level` is the authoritative routing key — a channel's tier is fixed in `Telemetry.json`, independent of which dashboard is active. If a tier has no active channels, the frame is sent as a 2-byte stub `[flag][0x20]`. The flag value is a monotonic counter assigned per connection; base+1 and base+2 are always exactly one and two above the base flag.

### Level-2000 frame (base+2)

Channels with `package_level: 2000` in `Telemetry.json`. Packed using the same bit-packing algorithm and alphabetical channel ordering as the base frame. Example layout with 6 level-2000 channels (104 bits = 13 bytes):

| Bits | Channel | Compression | Width |
|------|---------|-------------|-------|
| 0–31 | BestLapTime | `float` | 32 |
| 32–63 | LastLapTime | `float` | 32 |
| 64–73 | TyreWearFrontLeft | `percent_1` | 10 |
| 74–83 | TyreWearFrontRight | `percent_1` | 10 |
| 84–93 | TyreWearRearLeft | `percent_1` | 10 |
| 94–103 | TyreWearRearRight | `percent_1` | 10 |
| *(total)* | | | **104 bits = 13 bytes exactly** |

### Level-500 frame (base+1)

Channels with `package_level: 500` in `Telemetry.json`. If a dashboard has no active level-500 channels, this frame is sent as a 2-byte stub `[flag][0x20]`.

### Payload size

Each stream's payload carries only the channels from that dashboard that belong to that `package_level` tier. Channels within each stream are packed **alphabetically by URL suffix**.

Total payload bytes = `ceil(sum_of_channel_bit_widths / 8)`.

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

Channels are first grouped by **`package_level`** (30 → base frame, 500 → base+1, 2000 → base+2), then within each frame packed **alphabetically by URL suffix** (the part after `v1/gameData/`). Channels are iterated in sorted order by URL and packed sequentially into the bit stream starting at bit 0.

Bits are packed **LSB-first within each byte** (bit 0 = LSB of byte 0, bit 8 = LSB of byte 1, etc.). Multi-bit fields span byte boundaries when needed.

#### Example: F1 dashboard base frame (level-30 channels, alphabetical order)

Channels from the F1 dashboard with `package_level: 30`, sorted alphabetically by URL suffix, confirmed by capture (Gear at bit 79):

| Bits | Channel | Compression | Width |
|------|---------|-------------|-------|
| 0–9 | Brake | `float_001` | 10 |
| 10–41 | CurrentLapTime | `float` | 32 |
| 42 | DrsState | `bool` | 1 |
| 43–46 | ErsState | `uint3` | 4 |
| 47–78 | GAP | `float` | 32 |
| 79–83 | Gear | `int30` | 5 |
| 84–99 | Rpm | `uint16_t` | 16 |
| 100–115 | SpeedKmh | `float_6000_1` | 16 |
| 116–125 | Throttle | `float_001` | 10 |
| 126–127 | *(padding)* | | 2 |

---

## Telemetry startup sequence (from capture analysis)

Pit House sends several concurrent command streams when telemetry is active. Analysis of the `dash.ndjson` capture (which includes the pre-telemetry phase) shows the startup order:

### Concurrent outbound streams during active telemetry

| Stream | Rate | Device | Group/Cmd | Purpose | Required? |
|--------|------|--------|-----------|---------|-----------|
| Sequence counter | ~45/s | base (0x13) | `0x2D/F5:31` | Frame sync to base | TBD |
| Telemetry enable | ~48/s | wheel (0x17) | `0x41/FD:DE` data=`00:00:00:00` | Mode/enable flag | Likely — runs entire session |
| **Live telemetry** | ~31/s | wheel (0x17) | `0x43/7D:23` | Bit-packed game data | Yes |
| Heartbeat | ~1/s each | all devices (18–30) | `0x00` n=0 | Keep-alive / presence check | Likely |
| RPM LED position | ~4/s | wheel (0x17) | `0x3F/1A:00` | LED bar position | Separate feature |
| Telemetry mode | ~3/s | wheel (0x17) | `0x40/28:02` data=`01:00` | Set/poll multi-channel mode | Likely |
| Dash keepalive | ~1.5/s | dash (0x14), 0x15, wheel (0x17) | `0x43` n=1, data=`00` | Keep-alive for dash and wheel sub-devices | Yes — Pithouse sends to all three |
| Display config | ~1/s | wheel (0x17) | `0x43/7C:27` | Page-cycled display params | TBD |
| Status push | ~1/s | wheel (0x17) | `0x43/FC:00` | Session ack with session=FlagByte and current ack seq (NOT zeros) | Yes — Pithouse uses real session/seq |
| Settings block | ~1/s | wheel (0x17) | `0x43/7C:00` | Config sync | No (file transfer) |
| Button LED | ~1/s | wheel (0x17) | `0x3F/1A:01` | Button LED state | Separate feature |

### Telemetry startup timeline

Two captures provide complementary views of the startup sequence:

**Preamble detail** — from `moza-startup.json` (raw Wireshark JSON, 2026-04-12). This is the most precise source, decoded directly from raw USB packets with individual frame extraction:

| Offset | Frame | Notes |
|--------|-------|-------|
| +0.000 | `7c:00` type=0x81 session 0x01 + 0x02 | Opens two SerialStream sessions simultaneously |
| +0.009 | (IN) `fc:00` acks for both sessions | Wheel accepts immediately |
| +0.013 | (IN) `7c:00` data on session 0x02 | Wheel dumps channel registrations (v1/gameData/Rpm etc.) |
| +0.053-0.087 | `fc:00` acks (seq 04→17) | Host acks each incoming data chunk |
| +0.064-0.070 | `7c:00` tier definition TO wheel | Host sends tier config (channel indices, compression codes, bit widths) |
| +0.072 | First `7d:23` telemetry (flag=0x00) | Interleaved with acks — smaller "probe" tier, n=14 |
| +0.100-1.000 | `7d:23` flag=0x00 (~25 frames) | ~30Hz, heartbeats only — no 0x41 enable yet |
| +0.700-0.970 | Identity probes to wheel/base/pedals | Groups 0x00, 0x02-0x11 |
| +0.970 | **`0x0E` debug poll starts** | Parameter table reads at ~9Hz to 0x12/0x13/0x17 |
| +1.054 | **First `0x41/FD:DE` enable** | 1.05s after session opens |
| +1.089 | `0x40` channel config (1E, 09:00) | Deferred until after session exchange |
| +1.124-1.127 | `7c:00` additional config on session 0x02 | Second batch of tier data |
| +1.130 | **First `7d:23` with flag=0x02** (n=24) | Full telemetry — session exchange complete |
| +1.200 | Display sub-device probe | Identity commands via 0x43 (model="Display") |

**Full connect-to-telemetry** — from `connect-wheel-start-game.json` (wheel plugged in cold, then Assetto Corsa started):

| Phase | Time | Events |
|-------|------|--------|
| **Idle** | t=0–7.8s | Heartbeats, keepalives, `0x0E` debug poll. Only dev18/19/23 respond |
| **Wheel detected** | t=7.82s | Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05 → 0x07 → 0x0F → 0x11 → 0x08 → 0x10 |
| **Config burst** | t=8.2–9.1s | ~50 `0x40` commands (channel enables, page config, LED config). `0x40/28:02` polling at ~3 Hz |
| **Dashboard upload** | t=21.4–23.5s | `0x43/7c:00` chunked file transfer. Display sub-device probed |
| **Pre-game** | t=24–30.5s | `0x40/28:02` polling (response always `00:00`), heartbeats, keepalives |
| **Game starts** | t=30.568s | `0x41/FD:DE` enable + `0x2D/F5:31` seq counter start simultaneously |
| **Telemetry** | t=30.600s | `0x43/7D:23` live data (flag=0x02). ~31 frames/s steady state |

### SerialStream telemetry port (flag byte)

Pithouse's telemetry system runs over `MOZA::Protocol::SerialStreamManager`, a TCP-like reliable stream multiplexed over the serial connection. Each telemetry session opens a **port** on the wheel via a type=0x81 "session channel open" frame inside `0x43/7c:00`. The session port identifies which `7c:00` stream carries configuration data (tier definitions, acks).

**The flag byte in `7d:23` telemetry frames (byte offset 10) is NOT the session port number — it comes from the tier definition.** Pithouse uses a monotonic counter that starts at 0x00 for an initial "probe" batch, then increments for subsequent batches. Across captures the wheel accepts flags at 0x00, 0x02, 0x07, 0x0a, 0x13 — any value works as long as the tier definition and telemetry frames agree. The exact relationship between enable entry offsets and tier flag bytes is not fully understood; the plugin exposes a `FlagByteMode` setting (0=zero-based, 1=session-port-based, 2=two-batch) so the correct approach can be determined empirically.

**Port allocation uses a global monotonic counter** shared between host and wheel. Both sides allocate from the same counter space — the host picks low numbers (1, 2, 3...) while the wheel picks its own (6, 8, 9...). The next host allocation accounts for wheel-allocated ports (e.g. host session 3 gets port 0x0a because 3-9 were taken by the wheel). The counter resets on wheel power cycle.

**Observed session opens in `moza-startup.json` (2026-04-12, raw JSON):**

| Time | Source | Session byte | Port (payload) | Notes |
|------|--------|-------------|----------------|-------|
| 8.756s | Host | 0x01 | 0x0001 | First host session (management/upload) |
| 8.756s | Host | 0x02 | 0x0002 | Second host session (telemetry config) |
| 11.102s | Wheel | 0x08 | 0x0008 | Wheel-initiated keepalive |
| 11.102s | Wheel | 0x09 | 0x0009 | Wheel-initiated configJson RPC |
| 11.187s | Host | 0x03 | 0x000a | Third host session — port 10, not 3! |
| 11.894s | Wheel | 0x06 | 0x0006 | Wheel-initiated keepalive |

Key insight: the **session byte** (chunk header) and **port number** (payload) are different for session 0x03 — the session byte is a host-local identifier, the port is globally allocated. For sessions 0x01 and 0x02 they happen to match because those are the first allocations after power-on.

**Observed flag bytes across captures (confirmed from raw JSON, not ndjson):**

| Capture | Flag | Verified from |
|---------|------|---------------|
| `moza-startup.json` (today) | 0x02 | Raw JSON — first port after power-on |
| `burn-tyres.json` | 0x0a | Raw JSON — later connection |
| `0-100redline-0-main-dash.json` | 0x13 | Raw JSON — even later connection |

**Pithouse flag byte assignment (confirmed 2026-04-12 comparative captures):** Pithouse **always** uses 0-based flag bytes regardless of session port. In both `moza-startup-1` and `moza-startup-2` captures, tier definitions use flags 0x00, 0x01, 0x02 and the first telemetry frame uses flag=0x00 — even though the telemetry session was on port 0x02. Pithouse starts with flag=0x00 (the fastest tier) and sends all tier flags from the first frame. The earlier observation of a "transition from 0x00 to 0x02" was actually the separate tiers running at different rates, not a flag transition.

**Session open frame format:**

```
7E 0A 43 17 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [checksum]
                   └─chunk ID   └─seq(LE)=port       └─session_id(LE)   └─window=765
```

Pithouse opens **two sessions simultaneously** (0x01 and 0x02) in the same USB packet. The wheel responds with `fc:00` acks for both. The `fc:00` session bytes in steady state track the **session ack protocol** (incrementing ack_seq for each 7c:00 data chunk received), NOT the telemetry flag byte.

**Current plugin approach:** The plugin implements the observed Pithouse preamble sequence:
1. Probes for available ports (type=0x81 from port 1 upward, ~80ms per probe)
2. First two acked ports become management + telemetry sessions; telemetry port = FlagByte
3. Sends sub-message 1 preamble (14-byte `07...03...` message) on the telemetry session
4. Sends tier definition as 7c:00 data chunks on the telemetry session (flags 0x00+, NOT FlagByte+)
5. Sends Display sub-device identity probe via 0x43
6. Subscribes to incoming `MessageReceived` to ack telemetry session channel data with fc:00
7. Waits ~1 second for the session data exchange to complete (heartbeats only during this period)
8. Sends 0x40 channel config burst (1E enables, 28:00, 28:01, 09:00, 28:02)
9. Begins 0x41 enable signal and 7d:23 telemetry with flags 0x00, 0x01, 0x02

This matches Pithouse's observed timing: session opens first, sub-message 1 + tier def, ~1s of session data exchange with acks, then channel config, then telemetry+enable. The ~1s preamble delay is required — Pithouse does not send 0x41 or 0x40 until after the session exchange.

**Port probing:** The plugin probes for available ports by sending type=0x81 session opens starting from port 1, waiting ~80ms for an fc:00 ack on each. The first two ports that respond become the management and telemetry sessions. The telemetry session's port (FlagByte) is used only for 7c:00 session framing and fc:00 acks — NOT for the tier flag bytes in telemetry frames. This handles any counter state — whether the wheel was just powered on (ports 1-2 available) or SimHub's built-in support has consumed ports 1-N (the next free port is found automatically). The probe adds ~100-400ms to startup depending on how many ports must be skipped. `Start()` is dispatched to a background thread so the serial read thread stays free to deliver the fc:00 ack responses.

### Plugin startup sequence

The plugin replicates Pithouse's observed preamble with probe-based port allocation:

**Phase 0 — Port probe + config** (~100-400ms, before timer starts):
1. Send type=0x81 session opens from port 1 upward, wait ~80ms for fc:00 ack
2. First two acked ports become management (`_mgmtPort`, session 0x01) and telemetry (`FlagByte`, session 0x02) sessions
3. If `TelemetryUploadDashboard` is enabled, upload the `.mzdash` file on the management session via `DashboardUploader.BuildUploadMessage()` → `TierDefinitionBuilder.ChunkMessage()`. Wait up to 2s for wheel acknowledgment, then send type=0x00 end marker
4. Send sub-message 1 preamble (`07 04 00 00 00 02 00 00 00 03 00 00 00 00`) as 7c:00 data on the telemetry session — prepares the wheel's tier config parser
5. Send tier definition as 7c:00 data chunks on the telemetry session (channel indices, compression codes, bit widths — see § Tier definition protocol). **Flag bytes are 0x00-based, NOT session-port-based.**
6. Send Display sub-device identity probe via 0x43 (see § Display sub-device probe)

**Phase 1 — Preamble** (~1 second, timer running):
7. Ack incoming 7c:00 channel data on the telemetry session with fc:00 (session=FlagByte)
8. Send heartbeats only — no telemetry, no enable, no channel config
9. Detect Display sub-device from 0x87 model name response

**Phase 2 — Active** (continuous, after preamble):
10. Send `0x40` channel config burst (1E enables for pages 0-1 channels 2-5, then 28:00, 28:01, 09:00, 28:02)
11. Begin `0x41/FD:DE` enable signal (~30+ Hz)
12. Begin `0x43/7D:23` bit-packed telemetry (flags 0x00/0x01/0x02, ~30 Hz per tier)
13. Begin `0x2D/F5:31` sequence counter (~30 Hz)
14. Begin periodic streams at ~1 Hz: heartbeats, dash keepalives (0x43 to dev 0x14, 0x15, 0x17), display config (7C:27), session ack (FC:00 with session=FlagByte and current ack seq)
15. Begin `0x40/28:02` telemetry mode polling (~3 Hz)

The RPM LEDs (`0x3F/1A:00`) and button LEDs (`0x3F/1A:01`) are handled separately by `MozaDashLedDeviceManager` and `MozaLedDeviceManager` and work with zero preamble.

**Disable → re-enable:** `Stop()` resets `FramesSent` and the caller clears the dispatch guard, so re-enabling telemetry performs a full fresh startup (new port probing, new tier definition, new preamble). This is required because the wheel's session state may have changed while telemetry was disabled.

**Dashboard upload:** PitHouse uploads the `.mzdash` dashboard file to the wheel on **every connection** (confirmed across VGS and CSP captures). The plugin now implements this upload on session 0x01 (management port) using the FF-prefixed sub-message framing. The upload is sent before tier definitions, with a handshake that waits for the wheel's acknowledgment. Controlled by the `TelemetryUploadDashboard` setting (default: on).

---

## Other periodic commands

### Group 0x40 (host → device 0x17)

**Sub-command 0x28 — dashboard multi-function queries/set:**

| Wire | Name (rs21_parameter.db) | Purpose |
|------|--------------------------|---------|
| `28:00 data=00` | `WheelGetCfg_GetMultiFunctionSwitch` | Query active dashboard mode. The wheel retains its last loaded dashboard across disconnections. |
| `28:01 data=00` | `WheelGetCfg_GetMultiFunctionNum` | Query active page number |
| `28:02 data=01:00` | `WheelGetCfg_GetMultiFunctionLeft` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |

Pithouse sends 28:00 and 28:01 (read current state) followed by 28:02 (set mode) during the channel config burst. This is a read-then-write pattern — the wheel remembers its dashboard state across power cycles.

**Normal operation (~3.4 Hz):** `28 02 01 00` continues polling to maintain multi-channel mode.

**Dashboard upload:** burst of 18+ distinct payloads including channel enable/disable (`1E [0/1] [ch]`), page config (`1B [page] FF`), and various sub-commands.

### Group 0x2D (host → device 0x13, ~50 Hz)

Cmd `[0xF5, 0x31]`. Data: `00 00 00 XX` where XX increments by 1 each send. Sequence counter for the base unit.

### Group 0x0E poll (host → device 0x13, ~1 Hz)

3-byte payload `00 01 XX` with 16-bit BE countdown counter starting at 0x013A (314). Base echoes back + 4 unknown bytes.

### Group 0x1F (host → device 0x12, ~3 Hz)

`4F XX 00/01` where XX cycles `08`→`09`→`0A`→`0B`. Response inserts `0xFF` status byte.

### Group 0x0E — parameter table reader / debug console (host → devices 0x12/0x13/0x17, ~9 Hz)

Pithouse sends 158 of these per session. The host reads EEPROM parameters sequentially and receives firmware debug log output.

**Request format:** `7E 03 0E [device] 00 [table] [index] [checksum]`
- `table`: EEPROM table number (0x00 = base config, 0x01 = alt table)
- `index`: parameter index, incremented sequentially (0x01, 0x03, 0x04, ...)

**Response format (group 0x8E):**
- **Parameter values** (cmd=00:00, n=7): `[index] 00 00 [value bytes]` — stored parameter at that index
- **Debug log text** (cmd=05:xx, variable length): ASCII firmware log output, e.g.:
  - `"RFloss[avg:0.00000%] recvGap[avg:4.25699ms]"` — NRF radio stats
  - `"INFO]param_manage.c:340 Table 2, Param 43 Written: 0"` — EEPROM write confirmation

The debug log entries confirm that `0x40/1E` channel config commands write to EEPROM. This is diagnostic only — **not required for telemetry**.

Starts at ~1s after session opens. Sent to base (0x12, 51 frames), wheel (0x17, 68 frames), and pedals (0x13, 39 frames). The plugin does not implement this.

### Group 0x28 (host → device 0x13, occasional)

Queries device parameters from the base unit. Request format: `[sub_id] 00 00`. Response mirrors sub_id with 2 data bytes.

Observed in `connect-wheel-start-game.json` (sent twice, ~2s apart):

| Sub-cmd | Response value | Notes |
|---------|---------------|-------|
| `0x01` | `01 C2` (450) | Base parameter |
| `0x17` | `01 C2` (450) | Wheel (device 0x17) parameter — possibly FFB strength/range |
| `0x02` | `03 E8` (1000) | Base parameter |

### Group 0x29 (host → device 0x13, once during config)

Sent once during dashboard config burst. Payload: `13 04 4C` (device 0x13, value 1100). Response mirrors exactly. Possibly a timing/rate setting for the base.

### Group 0x2B (host → device 0x13, occasional)

`02 00 00`, sent on state changes (pause, session end).

### Group 0x43 sub-commands (device 0x17)

| Cmd ID | Data | Frequency | Notes |
|--------|------|-----------|-------|
| `[0xFC, 0x00]` | 3 bytes | varies | Session acknowledgment (`session + ack_seq`) |
| `[0x7C, 0x00]` | varies | varies | Session-based file transfer / RPC (see Dashboard upload protocol) |
| `[0x7C, 0x27]` | 4–8 bytes | ~1/s | Periodic display config push (page-cycled; see § Dashboard upload) |
| `[0x7C, 0x23]` | 8 bytes | once | Dashboard activation notification |

### Group 0x43 broadcast (devices 0x14, 0x15)

Short (length=2) packets to dash (device 20) and device 21 every ~5s. Heartbeat/sync.

---

## Dashboard upload protocol (group 0x43, cmd `7c:00`)

Pit House transfers dashboard files and configuration to the wheel using a proprietary TCP-like serial stream protocol (`MOZA::Protocol::SerialStreamManager`) over `0x43/7c:00`. The `fc:00` command is used for acknowledgments. This is NOT CoAP — CoAP is a separate layer used for device parameter management.

### Chunk format

Each `7c:00` data field contains one chunk:

```
session(1)  type(1)  seq_lo(1)  seq_hi(1)  payload(≤58)
```

| Field | Size | Description |
|-------|------|-------------|
| session | 1 | Session ID — pre-assigned, multiple concurrent sessions |
| type | 1 | `0x01` = data, `0x00` = control/end marker, `0x81` = session channel open (device-initiated) |
| seq | 2 LE | Sequence number (monotonic within session) |
| payload | ≤58 | Net data per chunk; **non-last data chunks have a 4-byte CRC-32 trailer** |

Net payload per full data chunk: **54 bytes** (58 minus 4-byte CRC). The last chunk in a transfer has no CRC trailer.

Acknowledgment packets use `fc:00` with 3 bytes: `session(1) + ack_seq(2 LE)`. The session ID in the ack identifies the **ack sender's** session, not the data sender's. Linked session pairs (e.g. 0x03↔0x0A) use cross-session acks.

### CRC algorithm

**Standard CRC-32** (ISO 3309 / ITU-T V.42, same as zlib/Ethernet/gzip/PNG):
- Polynomial: `0x04C11DB7` (reflected), init `0xFFFFFFFF`, xor-out `0xFFFFFFFF`
- Stored **little-endian** in the 4-byte trailer
- Covers only the **54-byte payload data** (excludes session/type/seq header)
- Per-chunk (not cumulative across chunks)
- Computable via `zlib.crc32(payload_bytes)` or `System.IO.Hashing.Crc32`

### Type 0x81 — session channel open

Device sends type `0x81` to initiate or acknowledge a session. Payload is 4 bytes:

```
session_id(2 LE)  receive_window(2 LE)
```

Observed: `04 00 fd 02` → session 4, window 765.

### Compressed transfer format

Zlib-compressed transfers (RPC messages, file contents) prepend a 9-byte header to the reassembled application data:

```
flags(1)  comp_sz(4 LE)  uncomp_sz(4 LE)  [zlib data...]
```

The zlib stream uses standard deflate (`78 9c` magic). Reassembly: strip the 4-byte CRC from each non-last chunk, concatenate all chunk payloads (excluding session/type/seq headers), then parse the 9-byte header and decompress `comp_sz` bytes.

### Concurrent session map (observed in `dash-upload.ndjson`)

8 concurrent sessions run during a single dashboard upload:

| Session | Duration | Role | Description |
|---------|----------|------|-------------|
| 0x01 | 8.5s | Management | Bidirectional RPCs with `0xFF`-prefixed messages (see below) |
| 0x02 | 6.9s | Keepalive | Dev→host, empty `00 00 00 00`, ~3.4s interval |
| 0x03 | 6.9s | Keepalive | Host→dev, linked to 0x0A via cross-session acks |
| 0x04 | 3.0s | **File transfer** | Path exchange + mzdash upload (75 data + 28 ack chunks) |
| 0x06 | 6.9s | Keepalive | Alternating directions, ~3.4s |
| 0x08 | 6.9s | Keepalive | Alternating directions, ~3.4s |
| 0x09 | 5.8s | **configJson RPC** | Dev sends dashboard state; host responds with dashboard list |
| 0x0A | 6.9s | Keepalive | Dev→host, linked to 0x03 |

Additionally, bare `0x43` frames (no cmd bytes, n=1, payload=`0x00`) are sent to devices 0x17/0x14/0x15 every ~1.1s as connection-level keepalive pings. Device replies `0x80`.

### Session 1 — management messages

Management RPCs use a `0xFF`-prefixed envelope:

```
FF(1)  inner_len(4 LE)  token(4 LE)  data(inner_len)  CRC32(4)
```

The token links requests to responses. Multi-chunk messages also have per-chunk CRC trailers. The message at t=5.2s in the capture carries a zlib-compressed device log (7163 bytes, UTF-16BE) listing all installed dashboards and rendering status.

### File transfer sequence (observed in `dash-upload.ndjson`)

The upload of a dashboard file involves multiple sessions and a post-transfer configuration burst:

**1. File path exchange + content push (session 4)**

The device initiates with a type=0x81 channel open. The host then sends two sub-messages:

**Sub-message 1 — path registration (no file content):**

```
header(8)
  TLV paths (0x8C=local, 0x84=remote)
  MD5_len(1=0x10) + MD5(16)
  reserved(4=0x00000000)
  token(4)
  sentinel(4=0xFFFFFFFF)
```

**Sub-message 2 — file content push:**

```
header(8)
  TLV paths (0x8C=local, 0x84=remote)
  MD5_len(1=0x10) + MD5(16)
  reserved(4)
  token(4) + token(4)
  file_count(4)
  dest_path_byte_len(4)
  dest_path(UTF-16BE, null-terminated)
  compressed_header + zlib_stream
```

**8-byte transfer header:**

| Byte | Host→dev | Dev→host | Meaning |
|------|----------|----------|---------|
| 0 | `0x02` | `0x01` | Sender role (0x02=host, 0x01=device) |
| 1 | `0x40` (64) | `0x38` (56) | Max chunk payload size |
| 2 | `0x01` | `0x01` | Transfer type (0x01=file transfer) |
| 3–7 | zeros | zeros | Reserved |

**TLV path markers:**

| Marker | Meaning |
|--------|---------|
| `0x8C` | Local path (host-side temp file) |
| `0x84` | Remote path (device-side staging or target) |

Each entry: `marker(1) + 0x00(1) + UTF-16LE_path(null-terminated)`. Scan to null terminator for length.

Host paths: `C:/Users/.../AppData/Local/Temp/_moza_filetransfer_tmp_{timestamp}`
Device staging: `/home/root/_moza_filetransfer_md5_{md5hex}`
Device target: `/home/moza/resource/dashes/{name}/{name}.mzdash`

Note: TLV paths use UTF-16LE, but the destination path in sub-message 2 uses **UTF-16BE**.

The file content (mzdash JSON) is zlib-compressed and embedded after the destination path.

End-to-end file integrity uses **MD5** (transmitted alongside paths). The on-device staging file is named after the MD5 hash.

**Session 4 sequence diagram:**

```
Device                                     Host
  │ ──── type=0x81 (channel open) ────────→  │  seq=0x0004
  │ ←─── fc:00 ACK ──────────────────────    │
  │ ←─── Sub-msg 1: path registration ───    │  7 chunks
  │ ──── fc:00 ACKs ─────────────────────→   │
  │ ──── Sub-msg 1 response (echo paths) ─→  │  6 chunks
  │ ←─── Sub-msg 2: file content push ───    │  32 chunks
  │ ──── fc:00 ACKs ─────────────────────→   │
  │ ──── Sub-msg 2 response ─────────────→   │  6 chunks
  │ ←─── type=0x00 end marker ───────────    │
  │ ──── type=0x00 end marker ───────────→   │
```

**2. Dashboard config RPC (session 9, compressed transfer)**

Host sends a `configJson()` message listing all dashboards:
```json
{"configJson()":{"dashboards":["Formula 1","GT V01",...,"rpm-only"],
  "dashboardRootDir":"","fontRootDir":"","fonts":[],"imageRootDir":"","sortTags":0},"id":11}
```

Device responds with dashboard management state:
```json
{"TitleId":4,"disabledManager":{"deletedDashboards":[],
  "updateDashboards":[{"createTime":"...","dirName":"rpm-only",
  "hash":"..."}]}}
```

**3. Channel configuration burst (group 0x40, after file transfer)**

After the file is transferred, Pit House sends a burst of `0x40` commands to configure the wheel's channel layout:

| Cmd | Data | Purpose |
|-----|------|---------|
| `09:00` | (none) | Begin/reset channel config |
| `1e:01` | `CC 00 00` | Enable channel CC on page 1 |
| `1e:00` | `CC 00 00` | Enable channel CC on page 0 |
| `1c:00`/`1c:01` | `00` | Page configuration |
| `1d:00`/`1d:01` | `00` | Page configuration |
| `28:00` | `00` | Query active dashboard mode (wheel retains across power cycles) |
| `28:01` | `00` | Query active page number |
| `28:02` | `01 00` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |
| various | — | Display settings (`0a`, `0b`, `05`, `1b`, `20`, `21`, `24`, etc.) |

**4. Periodic display config (group 0x43, cmd `7c:27`)**

Sent ~1/s after the dashboard is active. Two payloads per page, cycling through all dashboard pages. Values are **page-derived**, confirmed across 1-page (rpm-only) and 3-page (F1) dashboards:

| Page `p` | 8-byte payload | 4-byte payload |
|-----------|---------------|---------------|
| 0 | `0f 80 05 00 03 00 fe 01` | `0f 00 06 00` |
| 1 | `0f 80 07 00 05 00 fe 01` | `0f 00 08 00` |
| 2 | `0f 80 09 00 07 00 fe 01` | `0f 00 0a 00` |
| Formula | `0f 80 (5+2p) 00 (3+2p) 00 fe 01` | `0f 00 (6+2p) 00` |

Bytes `0f`, `80`/`00`, `fe 01` are constant. The page count equals the mzdash `children` array length.

A one-shot `7c:23` command is sent when a dashboard is first activated, with 8 bytes of display parameters.

### Wheel connection initialization (from `cs-to-vgs-wheel.ndjson`)

When a wheel connects (or is swapped), Pit House runs the full identity probe followed by a channel configuration burst — even without a dashboard upload. This was captured during a CS → VGS wheel swap:

1. **Identity probe** (groups 0x02–0x11): model name, HW/FW version, serial
2. **LED config** (`0x3f`): sleep color, sleep mode
3. **Channel configuration burst** (`0x40`): same commands as the post-upload burst:
   - `1e:01`/`1e:00 data=CC0000` — declare channel CC for page 0/1; wheel responds with `CC XXXX` (stored value, e.g. `01f4`=500, `03e8`=1000, `0bb8`=3000). These configure the telemetry stream, not the display — the CS V2.1 (no screen, RPM LEDs and buttons only) receives the same channel config as the VGS (built-in screen). Channel indices and response values are dashboard-specific
   - `1c`, `1d` — page config
   - `09:00` — config mode (response `09:28`)
   - `1b:00`/`1b:01` — brightness per page
   - `28:02 data=0100` — set multi-channel telemetry mode
   - `1f:00`/`1f:01` — LED color writes per index
4. **`0x0e` parameter polling**: reads wheel EEPROM registers (indices 0x01–0x14, then 0x2c+)
5. **`28:02 data=0100`** continues polling ~every 300ms after the burst

The wheel's `0x0e` debug log confirms channel config commands write to EEPROM: `"Table 2, Param 47 Written: 7614374"`.

**No `7c:00` file transfer or `configJson()` RPC occurs** — Pit House does not ask the wheel which dashboard it has active. It pushes the channel layout from its own internal state. The `0xc0/13:00` response `00 ff ff` during setup may indicate "no active dashboard" or a default state.

**Implication for SimHub plugin**: the channel configuration burst appears to be required on each wheel connection before telemetry frames will be accepted. Simply sending `7d:23` frames to a freshly connected wheel may not work without first sending the `0x40` channel enables and `28:02 data=0100`.

---

## Telemetry encode/decode formulas

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

Low-level EEPROM read/write protocol, applicable to any device. Bypasses the named command interface. Found in rs21_parameter.db but not observed in USB captures. See [serial.md § EEPROM direct access](serial.md#eeprom-direct-access-group-0x0a--10--any-device) for the command table.

EEPROM tables: 2=Base (38 params), 3=Motor (76 params, PID/encoder/field-weakening), 4=Wheel (123 params), 5=Pedals (45 params), 11=Unknown (8 params).

---

## Base ambient LED control (groups 0x20/0x22 — 32/34)

Controls 2 LED strips (9 LEDs each) on the wheelbase body. Group 32 = write, group 34 = read. Sent to the main device (0x12). Found in rs21_parameter.db but not observed in USB captures. See [serial.md § base ambient LEDs](serial.md#group-0x20--0x22-32--34--base-ambient-leds) for the command table.

---

## Wheel LED group architecture (groups 0x3F/0x40 — 63/64, extended)

The rs21_parameter.db reveals that newer wheels organize LEDs into **5 independently controlled groups**. See [serial.md § extended LED group architecture](serial.md#extended-led-group-architecture-groups-0x3f--0x40) for all per-group commands and additional newer wheel commands.

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |

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

---

## Setting value encoding notes

Several configuration commands use non-obvious value encoding. Confirmed by cross-referencing Pithouse USB captures with the foxblat source.

### Wheel settings (group 0x3F/0x40, device 0x17)

| Command | ID | Raw values | Notes |
|---------|-----|-----------|-------|
| paddles-mode | `03` | 1=Buttons, 2=Combined, 3=Split | **1-based**, not 0-based. Sending 0 is invalid and causes the firmware to break all paddle input including shift paddles |
| stick-mode | `05` | 0=Buttons, 256=D-Pad | 2-byte field; D-Pad mode sets the high byte (`0x0100`) |
| rpm-indicator-mode | `04` | 1=RPM, 2=Off, 3=On | **1-based** (wheel only) |

### Dashboard settings (group 0x32/0x33, device 0x14)

| Command | ID | Raw values | Notes |
|---------|-----|-----------|-------|
| rpm-indicator-mode | `11 00` | 0=Off, 1=RPM, 2=On | **0-based** — different from wheel |
| flags-indicator-mode | `11 02` | 0=Off, 1=Flags, 2=On | **0-based** |

Note: the wheel and dashboard use different base indices for indicator modes (wheel is 1-based, dashboard is 0-based).

See [serial.md](serial.md) and [serial.yml](serial.yml) for the full command tables.

---

## Telemetry data verification (2026-04-12)

Complete byte-level verification of the telemetry data frames confirmed:

**Frame structure:** Header `7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data] [checksum]` — all constant bytes, N computation, and checksum algorithm match Pithouse captures exactly. Verified checksums for session opens, mode frames, enable frames, and telemetry frames all produce byte-identical results to Pithouse.

**Bit-packing:** LSB-first algorithm in `TelemetryBitWriter` is correct. Channel sort order (case-insensitive by URL) matches the order observed in Pithouse captures. Decoding Pithouse telemetry data with the plugin's channel layout yields plausible game values (gears 0-6, RPM 0-7000, speed 0-260, brake/throttle 0-1).

**Encoding formulas:** All verified against capture data — `float_001` (×1000), `percent_1` (×10), `uint16_t` (direct), `float_6000_1` (×10), `int30` (5-bit, -1→31), `float` (IEEE 754), `bool` (0/1).

**F1 dashboard tier layout (3 tiers, all sizes match Pithouse):**

| Tier | Flag | Channels | Bits | Bytes | Pithouse bytes |
|------|------|----------|------|-------|---------------|
| Level 30 | 0x02 | Brake, CurrentLapTime, DrsState, ErsState, GAP, Gear, Rpm, SpeedKmh, Throttle | 126→128 | 16 | 16 ✓ |
| Level 500 | 0x03 | FuelRemainder | 10→16 | 2 | 2 ✓ |
| Level 2000 | 0x04 | BestLapTime, LastLapTime, TyreWear×4 | 104 | 13 | 13 ✓ |

**Bugs found and fixed:**
1. The `.mzdash` regex parser failed to match escaped-quote URLs (`Telemetry.get(\"v1/gameData/FuelRemainder\")`), silently dropping FuelRemainder (level-500 tier). This caused tier-to-flag misalignment: the plugin sent 13-byte level-2000 data on flag=0x03 where the wheel expected 2-byte level-500 data.
2. The plugin never sent the **tier definition message** — a critical 7c:00 data exchange on the telemetry session that tells the wheel firmware how to decode each flag byte's bit-packed data (channel indices, compression codes, bit widths per tier). Without this, the wheel cannot interpret 7d:23 frames at all. See § Tier definition protocol below.

## Tier definition protocol (group 0x43, session data on 7c:00)

Tier configuration uses a TLV (tag-length-value) encoding exchanged as 7c:00 session data chunks. The protocol is a **two-way handshake**: the wheel declares its channel catalog, then the host tells the wheel how to decode incoming telemetry.

### Handshake sequence (from bidirectional frame traces)

Before PitHouse opens sessions, the wheel already advertises its channel catalog via `7c:23` display config frames. The full handshake, traced frame-by-frame from both VGS (`moza-startup-1.pcapng`) and CSP (`pithouse-complete.txt`):

```
Phase 1 — Wheel advertisement (before session opens):
  Wheel sends 7c:23 display config frames at ~10Hz (alternating payloads)

Phase 2 — Session open + wheel channel catalog:
  Host  >>> 7C:00 SESSION_OPEN port=0x01, port=0x02 (both in same USB packet)
  Wheel <<< FC:00 ACK for both sessions (immediate)
  Wheel <<< 7C:00 session 0x01: tag 0x07 (version=0) + tag 0x0c (device hash)
                                + tag 0x01 + tag 0x05 + tag 0x04 ch=0 + tag 0x06 END
  Wheel <<< 7C:00 session 0x02: tag 0xff (sentinel) + tag 0x03 (value=1)
                                + tag 0x04 × N channel URLs + tag 0x06 END (total=N)
  Host  >>> FC:00 ACKs for wheel's channel data (incremental)

Phase 3 — Host tier config (format depends on wheel model):
  Host  >>> 7C:00 session 0x02: tier definition (version 0 or 2, see below)
  Host  >>> FC:00 ACKs continue for any remaining wheel data

Phase 4 — Telemetry starts:
  Host  >>> 7D:23 telemetry frames (~30 Hz)
  Host  >>> FD:DE enable signal (~30 Hz, starts ~1s after session open)

Phase 5 — Channel config burst (~1s after session open):
  Host  >>> 0x40 1E:xx channel enables, 28:00, 28:01, 09:00, 28:02
  Host  >>> Second batch of tier definitions (real dashboard tiers at higher flags)
```

Both VGS and CSP follow this exact sequence. The wheel always declares version 0 (`tag 0x07 param=1 value=0x00`) — both models send identical version tags. PitHouse decides the host→wheel response format based on the wheel's model name (from the 0x87 identity response), not from the version tag.

**Timing note:** On VGS, PitHouse starts sending telemetry (flag=0x00, 11B probe tier) at t+0.3s after session open, BEFORE the enable signal or channel config. The enable signal starts at t+1.0s, and the real dashboard telemetry (flag=0x03, 16B) starts at t+1.5s after the second tier definition batch.

### Session 0x01 — device description (both directions, both models)

Both the wheel and PitHouse send a short descriptor on session 0x01. Structure is identical:

```
[0x07] [01 00 00 00] [00]                     — version 0
[0x0c] [size] [data...]                        — device-specific hash/fingerprint
[0x01] [size: u32 LE] [data...]               — descriptor body
[0x05] [00]                                    — unknown
[0x04] [size] [ch_index=0] [url or padding]   — single channel entry (index 0)
[0x06] [00]                                    — end
```

Tag 0x0c (14 bytes) differs per device — VGS: `0c 06 69 42 07 14 e8 06...`, CSP: `0c 04 8a e5 d0 86 b2 fc...`. May encode hardware ID or firmware fingerprint. The channel entry at index 0 appears to be padding (3 ASCII spaces on VGS).

### Session 0x02 — channel catalog (wheel → host, both models)

The wheel sends its supported channels. Confirmed identical structure from VGS and CSP:

```
[0xff]                                         — sentinel / reset marker
[0x03] [04 00 00 00] [01 00 00 00]            — config param (value=1, constant across models)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

VGS reports 16 channels (BestLapTime, Brake, CurrentLapTime, DrsState, ErsState, FuelRemainder, GAP, Gear, LastLapTime, Rpm, SpeedKmh, Throttle, TyreWear×4). CSP reports 20 channels (adds ABSActive, ABSLevel, TCActive, TCLevel, TyrePressure×4, TyreTemp×4).

The channel catalog tells the host what the currently loaded dashboard subscribes to. Channel indices are 1-based, sorted alphabetically by URL.

### Session 0x02 — host response: version 0 URL subscription (CSP)

For CSP, PitHouse responds on session 0x02 with the same tag 0x04 format — echoing back the channel URLs as a subscription confirmation. The wheel firmware knows compression types internally.

```
[0xff]                                         — sentinel / reset
[0x03] [04 00 00 00] [01 00 00 00]            — config (value=1)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel subscription (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

PitHouse sends this twice in rapid succession (first immediately after session open, then again after acks arrive). Confirmed from `CSP captures/pithouse-complete.txt` (20 channels, identical to wheel catalog).

### Session 0x02 — host response: version 2 compact tier definitions (VGS)

PitHouse sends a different format: flag bytes, channel indices, compression codes, and bit widths. The wheel is told exactly how to decode the bit stream.

**Session preamble (same session as tier defs):**
```
[0x07] [04 00 00 00] [02 00 00 00]            — version 2
[0x03] [00 00 00 00]                           — config (value=0)
```

**Tier definition:**
```
[0x01] [size: u32 LE] [flag_byte]            — tier definition header
  [ch_index: u32 LE] [comp: u32 LE]         — 16-byte channel entry (repeated)
  [bits: u32 LE]     [reserved: u32 LE]
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

Optionally followed by enable entries and a second batch of tier definitions:
```
[0x00] [01 00 00 00] [flag_offset]           — tier enable (repeated per tier)
[0x01] ...                                    — second batch of tier defs at higher flag values
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker with actual channel count
```

Pithouse sends two batches: a first "probe" batch at flags 0x00+ with `total_channels=0`, then a second "real" batch at higher flags with the actual dashboard channels and total count. The wheel accepts telemetry on flags from either batch (observed: 0x00, 0x02, 0x07, 0x0a, 0x13 across captures).

**Channel indices** are 1-based, assigned alphabetically by URL across all tiers (not per-tier).

**Compression codes** (8 confirmed from F1 capture ✓, 11 inferred from Telemetry.json):

| Code | Type | Bits | Status |
|------|------|------|--------|
| 0x00 | bool | 1 | ✓ confirmed |
| 0x01 | uint8 / uint8_t | 4 / 8 | inferred |
| 0x02 | int8_t | 8 | inferred |
| 0x03 | uint15 | 4 | inferred |
| 0x04 | uint16_t | 16 | ✓ confirmed |
| 0x05 | int16_t | 16 | inferred |
| 0x07 | float | 32 | ✓ confirmed |
| 0x08 | int32_t | 32 | inferred |
| 0x09 | uint32_t | 32 | inferred |
| 0x0A | double | 64 | inferred |
| 0x0B | location_t | 64 | inferred |
| 0x0D | int30 | 5 | ✓ confirmed |
| 0x0E | percent_1 | 10 | ✓ confirmed |
| 0x0F | float_6000_1 | 16 | ✓ confirmed |
| 0x10 | tyre_pressure_1 | 12 | inferred |
| 0x11 | tyre_temp_1 | 14 | inferred |
| 0x12 | track_temp_1 | 14 | inferred |
| 0x13 | oil_pressure_1 | 14 | inferred |
| 0x14 | uint3 | 4 | ✓ confirmed |
| 0x15 | float_600_2 | 16 | inferred |
| 0x16 | brake_temp_1 | 16 | inferred |
| 0x17 | float_001 | 10 | ✓ confirmed |

Inferred codes are assigned sequentially by factory ID order from Telemetry.json. CSP uses version 0 (URL-based) which doesn't need codes — the wheel firmware resolves compression by URL. Code 0x06 is unassigned (gap between int16_t and float).

### Tag 0x03 — config parameter

Tag 0x03 has different values depending on direction and version:

| Direction | Version | Value | Interpretation |
|-----------|---------|-------|---------------|
| Wheel → Host | 0 | 1 | Constant across VGS and CSP |
| Host → Wheel | 0 (CSP) | 1 | Mirrors wheel value |
| Host → Wheel | 2 (VGS) | 0 | Different meaning in version 2 context |

### Chunking (both versions, both directions)

All 7c:00 session data uses SerialStream chunks with CRC-32 trailers (standard ISO 3309). **ALL chunks have CRC-32 trailers, including the final chunk** — verified by computing CRC-32 of every chunk's net data across multiple captures. Max 54 net bytes per chunk (58 with CRC).

### Current plugin implementation

The plugin supports both versions, selectable via `TelemetryProtocolVersion` setting (UI: Telemetry > Advanced > Protocol version):

- **Version 2** (default): sends compact numeric tier definitions via `TierDefinitionBuilder.BuildTierDefinitionMessage()`. Flag byte assignment controlled by `FlagByteMode` (0=zero-based, 1=session-port, 2=two-batch).
- **Version 0**: sends URL subscription via `TierDefinitionBuilder.BuildV0UrlSubscription()`. Double-sent (once at startup, once after preamble) to match PitHouse's observed behavior. Flag byte mode is not applicable — always uses zero-based.

Dashboard upload is controlled by `TelemetryUploadDashboard` setting (UI: Telemetry > Advanced > Upload dashboard, default: on). When enabled, the plugin uploads the `.mzdash` file to the wheel on session 0x01 (management port) using the FF-prefixed sub-message framing before sending tier definitions. The mzdash content is loaded from the user-selected file or from an embedded resource matching the active profile name.

The plugin parses the wheel's incoming channel catalog (session 0x02 tag 0x04 URLs) during the preamble phase and displays the detected channels in the UI. This confirms which channels the currently loaded dashboard subscribes to.

Session 0x01 carries different data in each direction. The wheel sends a short identity record (tag 0x07 version, tag 0x0c device hash — ~42 bytes). The plugin sends the compressed `.mzdash` dashboard file via `DashboardUploader.BuildUploadMessage()`, chunked with `TierDefinitionBuilder.ChunkMessage()`.

**PitHouse re-uploads the dashboard on every connection** — confirmed in `moza-unplug-plug-wheel-to-base.pcapng` (VGS, wheel reconnect while PitHouse running) and `CSP captures/pithouse-complete.txt` (CSP, full startup). In both captures, session 0x01 fills with compressed dashboard data immediately after session open. PitHouse does not check what's already loaded — it always pushes from its internal state. This may be a prerequisite for telemetry.

**Session 0x01 upload wire format** (confirmed by CRC-32 verification across VGS and CSP captures):

Each sub-message uses this framing:
```
[FF] [payload_size: u32 LE] [payload bytes]
[remaining_transfer_size: u32 LE]
[CRC32: u32 LE]                              ← covers ALL preceding bytes from FF through remaining_size
```

Three sub-messages are sent:

| Field | Payload size | Content | Notes |
|-------|-------------|---------|-------|
| 0 | 16 bytes | Device tokens (session-specific, differs per wheel) | remaining = total size of fields 1+2 |
| 1 | 8 bytes | `9e 79 52 7d 07 00 00 00` — protocol constant | Identical between VGS and CSP. remaining=3 |
| 2 | varies (VGS: 1350, CSP: 100) | Compressed mzdash content | 12B pre-header + zlib stream (last field, no remaining/CRC trailer) |

Each field except the last is followed by `remaining_transfer_size(4 LE) + CRC32(4)`. The CRC covers all bytes from `FF` through `remaining_transfer_size`. Field 2 is the last field and has no trailing remaining/CRC.

**Field 2 pre-zlib header** (12 bytes before the `78 da` zlib magic):
```
[CRC32_or_hash: 4B] [08 00 00 00: constant] [uncompressed_size_BE: 4B]
```

The zlib-compressed content IS the mzdash dashboard file — confirmed by partial decompression producing UTF-16LE channel names (`RpmAbsolute1`, etc.).

See § Dashboard upload protocol below for the session 4 explicit upload format (different framing, device-initiated).

## Display sub-device probe (group 0x43 identity commands)

The wheel may contain an internal Display sub-device (for wheels with built-in screens like the VGS). Pithouse probes this at ~t=9.97s — AFTER telemetry starts (t=9.88), so it's not a prerequisite for telemetry. The probe uses the same identity commands as the main wheel probe but routed via group 0x43 to reach the Display sub-module.

**Probe sequence** (from `moza-startup.json` 2026-04-12):

| Step | Frame | Response | Description |
|------|-------|----------|-------------|
| 1 | `7E 01 43 17 00 [cs]` | `80` | Heartbeat/ping |
| 2 | `7E 01 43 17 09 [cs]` | `89 00 01` | Presence check (1 sub-device) |
| 3 | `7E 05 43 17 04 00 00 00 00 [cs]` | `84 01 02 08 06` | Hardware ID |
| 4 | `7E 01 43 17 06 [cs]` | `86` + 13 bytes | Serial number |
| 5 | `7E 02 43 17 02 00 [cs]` | `82 02` | Product type |
| 6 | `7E 05 43 17 05 00 00 00 00 [cs]` | (version data) | Firmware query |
| 7 | `7E 02 43 17 07 01 [cs]` | `87 01 "Display"` | **Model name** |
| 8 | `7E 02 43 17 0F 01 [cs]` | `8F 01 "RS21-W08-HW SM-D"` | FW version part 1 |
| 9 | `7E 02 43 17 08 01 [cs]` | `88 01 "RS21-W08-HW SM-D"` | HW version part 1 |
| 10 | `7E 02 43 17 0F 02 [cs]` | `8F 02 "U-V14"` | FW version part 2 |

The plugin sends steps 1-10 during the preamble. The 0x87 response with model name "Display" confirms the sub-device is present, setting `DisplayDetected=true`. This is used to gate dashboard telemetry features in the UI — wheels without a display (e.g. CS V2.1 with RPM LEDs only) won't respond to this probe.

## Open questions

- ~~Value scaling for specialized types~~ — **RESOLVED**: All conversion formulas determined. Key insight: the `percent_1` scale factor is exactly 10.0 (not 10.22 as previously estimated from capture data)
- ~~CRC algorithm~~ — **RESOLVED (corrected 2026-04-12)**: Standard CRC-32 (ISO 3309), same as `zlib.crc32()`. Little-endian, covers per-chunk net data only. **ALL chunks have CRC-32 trailers, including the final chunk** — verified by computing CRC-32 against every chunk in multiple captures. Previous assumption that the last chunk omitted CRC was wrong.
- ~~File transfer header format~~ — **RESOLVED**: 8-byte header: role(1) + max_chunk_size(1) + transfer_type(1) + reserved(5). TLV paths use markers 0x8C (local) and 0x84 (remote) with UTF-16LE. See § Session 4 wire format in pithouse-re.md.
- ~~Session lifecycle~~ — **RESOLVED (2026-04-12, corrected)**: Sessions are opened via type=0x81 frames with port numbers from a global monotonic counter. Both host and device can open sessions. The host probes for available ports by sending type=0x81 and waiting for fc:00 acks. See § SerialStream telemetry port.
- ~~Protocol identity~~ — **RESOLVED**: The 0x43/7c:00 framing is `MOZA::Protocol::SerialStreamManager`, a proprietary TCP-like reliable stream. NOT CoAP. CoAP (libcoap 4.3.4) is a separate layer for device parameter management.
- ~~Flag byte / SerialStream port~~ — **PARTIALLY RESOLVED (2026-04-12, corrected 2026-04-13)**: The session port (FlagByte) is used for 7c:00 framing. The flag byte in tier definitions and telemetry frames is a separate value. Pithouse uses a monotonic counter starting at 0x00 for a "probe" batch, then higher values for actual dashboard tiers. Cross-capture verification (7 pcapng files) shows the wheel accepts flags at 0x00, 0x02, 0x07, 0x0a, 0x13 — the value doesn't matter as long as the tier definition matches the telemetry frames. The exact mapping between enable entry offsets and tier flag bytes is **not fully understood**. The plugin exposes `FlagByteMode` (0=zero-based, 1=session-port, 2=two-batch) for empirical testing. The CRC-32 fix (all chunks) was likely the critical change; the flag byte value change may have been incidental.
- ~~Session ID / port allocation~~ — **RESOLVED (2026-04-12)**: Session IDs (chunk header byte) and port numbers (payload) are **independent**. Port counter is global and monotonic within a power cycle. Plugin probes from port 1 upward.
- ~~Tier definition protocol~~ — **RESOLVED (2026-04-12, corrected 2026-04-12)**: Pithouse sends a 14-byte sub-message 1 preamble before the tier definition. Tag 0x07 (value=2) is a protocol version constant; tag 0x03 (value=0) is the base flag offset. The tier definition uses 0-based flag bytes. All session data chunks include CRC-32 trailers (including the final chunk). Plugin now sends sub-message 1, 0-based tier definitions, and correct CRC on all chunks. See § Tier definition protocol.
- ~~0x40/28:00 and 28:01 purpose~~ — **RESOLVED (2026-04-12)**: `WheelGetCfg_GetMultiFunctionSwitch` and `WheelGetCfg_GetMultiFunctionNum` (from rs21_parameter.db). These query the wheel's active dashboard mode and page number — the wheel retains its last loaded dashboard across power cycles. Pithouse reads current state (28:00, 28:01) then sets multi-channel mode (28:02). See § Group 0x40.
- ~~Gear encoding for reverse~~ — **RESOLVED**: `int30` is a signed 5-bit value: -1=R, 0=N, 1–12=gears. Reverse is stored as 31 (two's complement -1 in 5 bits).
- **Dashboard byte limit configuration** — stored at config object offset `+0x30`, set during dashboard upload (group 0x40). Exact mechanism for setting this limit not yet traced
- **Cold-start initialization** — `connect-wheel-start-game.json` captures wheel connection → game start, confirming the full init sequence (identity probe → config burst → dashboard upload → telemetry). EEPROM persistence across power cycles is confirmed for channel config; unclear for session state
- **MDD (standalone dash)** — no captures of telemetry sent to device 0x14; protocol may differ
- ~~Tier definition version selection~~ — **RESOLVED (2026-04-14)**: PitHouse sends version 2 (compact) to VGS and version 0 (URL-based) to CSP. The version is not negotiated from the wheel's tag 0x07 — PitHouse likely maps model name → version. Plugin supports both versions via `TelemetryProtocolVersion` setting. Tag 0x0c data (14 bytes, differs per wheel) may encode capabilities or firmware version — could be used to auto-select version in the future
- ~~Wheel channel catalog parsing~~ — **RESOLVED (2026-04-13)**: The plugin now buffers incoming 7c:00 tag 0x04 URL data during the preamble and parses the wheel's channel catalog. Detected channels are displayed in the UI and logged. Could be used to auto-build profiles or validate the active dashboard
- ~~Compression codes for non-F1 types~~ — **RESOLVED (2026-04-14)**: All 19 compression codes are implemented in TierDefinitionBuilder.cs. 8 codes confirmed from F1 dashboard capture (bool, uint3, int30, uint16_t, float, float_001, float_6000_1, percent_1); 11 additional codes (brake_temp_1, tyre_temp_1, tyre_pressure_1, track_temp_1, oil_pressure_1, float_600_2, location_t, int32_t, uint32_t, double, uint8_t) are inferred and functional. CSP uses version 0 (URL-based) which doesn't need compression codes — the wheel firmware resolves them by URL
- ~~Dashboard upload as telemetry prerequisite~~ — **IMPLEMENTED (2026-04-13)**: Plugin now uploads the `.mzdash` dashboard file on session 0x01 using the FF-prefixed sub-message framing (3 fields: device tokens, protocol constant, zlib-compressed mzdash content). CRC-32 verified format. Controlled by `TelemetryUploadDashboard` setting. The `configJson()` RPC (session 0x09) is not yet implemented — add if upload alone is not sufficient.
- **Dashboard upload: remaining field semantics** — Both "remaining" values are fixed constants across all 8 observed sessions (VGS and CSP, dashboard sizes from 100B to 1350B compressed): field 0 remaining = `7200` (0x1C20), field 1 remaining = `3`. NOT byte counts — semantics unknown (possibly buffer size, transfer window, or protocol parameters). Plugin now uses these constants.
- ~~Dashboard upload: field 0 device tokens~~ — **RESOLVED (2026-04-14)**: The 16-byte field 0 payload is two 8-byte LE values: token 1 = `[random_u32 | 0x00000002]`, token 2 = `[unix_timestamp | 0x00000000]`. Confirmed from 8 sessions across VGS and CSP: token 2 is always a Unix timestamp of session start; token 1 high 32 bits are always `0x00000002` (protocol version or request type); token 1 low 32 bits are CSPRNG output (no deterministic relationship to timestamp — tested CRC-32, FNV-1a, DJB2, MurmurHash3, mt19937, 12 LCG variants, crypto hashes, all negative). These are correlation IDs, not validated by the wheel. PitHouse's `Sync_DashboardManager` uses `mcUid` (STM32 MCU hardware UID, read via `MainMcuUidCommand`) as a per-device routing key, but mcUid is NOT encoded in the upload tokens.
- **Dashboard upload: per-field pacing** — Plugin sends all upload chunks (across all 3 FF-prefixed fields) in a single burst, then waits for ack. PitHouse may instead pace by field: send field 0 chunks → wait for ack → send field 1 → wait → send field 2. The burst approach matches how tier definitions are sent (also tight-loop, working). If large dashboards fail while small ones succeed, try adding per-field ack waits.
- **Dashboard upload: seq=2 assumes port=1** — Data chunks on the mgmt session start at seq=2, assuming session open used seq=1 (i.e. mgmtPort=1). The session open frame uses seq=port, so data should start at port+1. Since the serial port is exclusive (PitHouse cannot run simultaneously), port probing always finds ports 1 and 2, making seq=2 correct in practice. Same assumption exists in tier definition code (seq=3, assumes telemetry port=2). If this ever changes (e.g. multi-client over network), both need to use `port + 1` instead of hardcoded values.
- **EEPROM direct access** — group 10 protocol found in rs21_parameter.db but never observed in USB captures; needs live verification
- **Base ambient LEDs** — groups 32/34 commands found in rs21_parameter.db; not captured in USB traces (requires base with LED strips)
- **Wheel LED groups 2-4** — Single, Rotary, and Ambient groups found in rs21_parameter.db with up to 56 LEDs; only groups 0 (Shift/RPM) and 1 (Button) confirmed in captures so far
- **Group 0x09 semantics** — presence/ready check sent first during probe. Response `00 01` may indicate sub-device count (VGS has 1 Display sub-device). Needs verification with other wheel models
- **Group 0x28 / 0x29 purpose** — group 0x28 queries base for per-device parameters (values 450, 1000 seen); group 0x29 sets a base parameter (value 1100). Possibly FFB or calibration related
- ~~0x40/28:02 response discrepancy~~ — **RESOLVED (2026-04-14)**: Wheel responds `00:00` to `28:02 data=01:00` — this is normal behavior, not a failure. The plugin sends 28:02 during the preamble and telemetry flows regardless of the response value
- ~~Display sub-device identity probe~~ — **RESOLVED (2026-04-12)**: Pithouse probes a Display sub-module inside the wheel via 0x43 frames at ~t=9.97s (AFTER telemetry starts at t=9.88, so not a prerequisite). The plugin now sends the same probe during the preamble and detects the "Display" model name from the 0x87 response. Used to gate dashboard telemetry features in the UI. See § Display sub-device probe below
- ~~SerialStream SYN handshake~~ — **RESOLVED (2026-04-14)**: The three-way handshake (SYN1/SYN2/SYN3) exists in binary strings but is never needed. Type=0x81 session opens work without it — the SYN handshake is a lower-level connection layer already established when the serial port opens
- ~~Group 0x0E debug poll~~ — **RESOLVED (2026-04-12)**: Parameter table reader + firmware debug console. Pithouse reads EEPROM params sequentially at ~9Hz and receives ASCII debug log output (NRF radio stats, EEPROM write confirmations). Diagnostic only — not required for telemetry. See § Group 0x0E
