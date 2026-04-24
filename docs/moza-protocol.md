# Moza Racing serial protocol

> **Disclaimer:** Work in progress. May contain errors, omissions, or incorrect information. Research ongoing.

## Frame format

```
7E  [N]  [group]  [device]  [payload: N bytes]  [checksum]
```

| Field | Size | Description |
|-------|------|-------------|
| Start | 1 | Always `0x7E` |
| N | 1 | Byte count of payload only (excludes group, device, checksum) |
| Group | 1 | Request group / command category |
| Device | 1 | Target device ID on internal serial bus |
| Payload | N | Command ID (1+ bytes) followed by value bytes |
| Checksum | 1 | See below |

**N (payload length)** bounded: valid range 1–64. Values outside indicate corruption or desync — discard and rescan for next `0x7E`.

**Frame sync:** receivers scan byte stream for `0x7E`, discarding non-`0x7E` bytes. Once found, next byte read as N. If N out of range or checksum fails, frame dropped and scanning resumes. Self-synchronizing after corruption or mid-stream connection.

Command IDs that are integer arrays must be provided sequentially in order. Values big-endian. Multiple frames can be concatenated in a single USB bulk transfer.

### Checksum

`checksum = (0x0D + sum of all preceding bytes including 0x7E) % 256`

Magic value 13 (`0x0D`) incorporates USB endpoint (`0x02`), transfer type (`0x03` for URB_BULK), length constant (`0x08`). Changing it causes devices to not respond — likely firmware quirk.

### Checksum / body byte escape (0x7E byte stuffing)

When a computed checksum equals `0x7E`, sender **doubles it on wire** — transmits `0x7E 0x7E` instead of single `0x7E`. Receiver must consume extra byte after reading a frame whose checksum is `0x7E`. Without this, escape byte misinterpreted as start of new frame, desyncing subsequent parsing.

Applies to **both directions**. Confirmed from Wireshark USB captures (2026-04-18):

```
Host → device:  7e 06 3f 17 1a 01 3d 3f 00 00 7e 7e
                └── frame (cksum=0x7e) ──────────┘ └─ escape byte

Three 0x7E in a row (escaped checksum + next frame start):
Device → host:  7e 07 8e 21 00 00 0b 00 00 00 32 7e 7e 7e 07 8e 91 ...
                └── frame 1 (cksum=0x7e) ────────┘  │  └── frame 2 ─
                                              escape ┘
```

Three-`7E` case: first is checksum, second is escape, third is next frame start.

**Buffer parsing:** When extracting frames from concatenated USB bulk data, parser must skip escape byte between frames. Byte-at-a-time serial readers must consume one extra byte after frame with checksum `0x7E`. Failure causes escape `0x7E` read as frame start, next byte consumed as length field — typically a large value (e.g. `N=0x7E`=126) overshooting buffer, silently dropping subsequent frames.

**Scope:** Group IDs (0x07–0x64), device IDs (0x12–0x1E), and response transforms (group | 0x80, nibble-swapped device) never equal `0x7E`. However, **payload bytes CAN be `0x7E`** — observed in zlib-compressed session data (dashboard uploads) and device catalog frames. Host escapes every `0x7E` in body on wire by doubling. Frame boundary always 1 or 3 bytes of `0x7E` (single start, or escaped checksum + next start), never 2 — so `0x7E 0x7E` mid-frame is always escaped body/checksum byte, not boundary.

**Checksum computed on wire bytes (after escaping).** Host computes `(0x0D + sum)` over escaped representation. Each `0x7E` in decoded body (positions 2 through end-1) adds extra `0x7E` to wire-level sum. Receivers: `verify(frame)` adds `frame[2:-1].count(0x7E) * 0x7E` to computed checksum. `build_frame()` does same when computing outgoing checksum.

**Plugin impl note (2026-04-22):** the SimHub plugin previously used a raw-sum `CalculateChecksum()` that omitted the escape-count term, causing ~20% of zlib-bearing session chunks (configJson state, dashboard uploads) to fail verify and be silently dropped when their compressed payloads contained `0x7E` bytes. Fixed by routing all production send/verify paths through `MozaProtocol.CalculateWireChecksum()` which adds `count(0x7E in body positions 2..len-1) × 0x7E` to the sum.

Reference: [boxflat PR #131](https://github.com/Lawstorant/boxflat/pull/131).

### Responses

| Field | Transform |
|-------|-----------|
| Group | Request group + `0x80` (MSB set) — e.g. `0x21` → `0xA1` |
| Device | Nibbles swapped — e.g. `0x13` → `0x31` |
| Payload length | Reflects response data size, not request |

Write requests: response mirrors request payload. Read requests: response contains full stored value regardless of request size (1-byte read probe returns full 16-byte string).

### Known wheel write echoes

Certain writes to device `0x17` (wheel) are echoed verbatim by firmware though they carry no read-back semantics (LED index, brightness, channel CC vary per call so payload-keyed replay table can't cover them). Plugin recognizes echoes via `MozaProtocol.WheelEchoPrefixes` / `IsWheelEcho()` and treats them as wheel-alive signals without logging "unmatched". Mirror of `sim/wheel_sim.py:_WHEEL_ECHO_PREFIXES`:

| Group | Device | Prefix (first bytes of payload) | Purpose |
|-------|--------|---------------------------------|---------|
| 0x3F | 0x17 | `1f 00` / `1f 01` | Per-LED color page 0/1 |
| 0x3F | 0x17 | `1e 00` / `1e 01` | Channel CC enable page 0/1 |
| 0x3F | 0x17 | `1b 00` / `1b 01` | Brightness page 0/1 |
| 0x3F | 0x17 | `1c 00` / `1d 00` / `1d 01` | Page config |
| 0x3F | 0x17 | `27 00..05` | LED group colour (group 0 = RPM, 1..5 = rotary knobs) |
| 0x3F | 0x17 | `2a 00..03` | Unknown paged commands |
| 0x3F | 0x17 | `0a 00`, `24 ff`, `20 01` | Mode / display / idle-mode |
| 0x3F | 0x17 | `1a 00` | RPM LED telemetry write |
| 0x3F | 0x17 | `19 00` / `19 01` | RPM / button LED color write |
| 0x3E | 0x17 | `0b` | Newer-wheel LED command (1-byte prefix) |

### Command chaining

Multiple commands can be sent at once. Responses **not guaranteed in request order** — match by group number.

---

## USB topology

Device: Moza composite USB (VID `0x346E` PID `0x0006`).

| Interface | Type | Endpoints | Purpose |
|-----------|------|-----------|---------|
| MI_00 | USB serial (CDC) | 0x02 OUT / 0x82 IN | Moza protocol bus — all serial frames |
| MI_02 | HID | 0x03 OUT / 0x83 IN | Wheel axes/buttons (not telemetry) |

Device IDs (19=base, 20=dash, 23=wheel, etc.) are addresses on internal serial bus routed through wheelbase hub — not separate USB devices.

**All captured live telemetry addressed to device 0x17 (wheel).** No captures exist of telemetry sent to device 0x14 (MDD / standalone dash).

---

## Device and command reference

See [serial.md](serial.md) for full device ID and command list.

### Authoritative source: rs21_parameter.db

Pit House installation contains `bin/rs21_parameter.db` — SQLite DB with 919 commands across 23 groups. Canonical reference for RS21 (sim racing) device commands: names, descriptions, request/response group encoding, payload sizes, data types, valid ranges, EEPROM addresses. `request_group` field encodes as JSON array: first element = protocol group byte, remaining elements = command ID bytes. Example: `[40, 2]` → group 0x28, cmd 0x02.

Commands NOT in DB (discovered via USB captures): identity queries (groups 7/8/15/16), music sub-commands (group 42), sequence counter (group 45), telemetry enable (group 65), live telemetry stream (group 67/0x43).

---

## Device identity & probes

### Wheel connection probe sequence

When wheel detected, Pithouse queries device 0x17 for identity. All identity strings are 16-byte null-padded ASCII.

Observed probe order (`connect-wheel-start-game.json`): 0x09, 0x04, 0x06, 0x02, 0x05, 0x07, 0x0F, 0x11, 0x08, 0x10.

| Group | Cmd ID | Response | Notes |
|-------|--------|----------|-------|
| 0x09 | — (n=0) | 2 bytes (e.g. `00 01`) | **Presence/ready check** — sent first. Response may indicate sub-device count |
| 0x02 | — | 1 byte (e.g. `0x02`) | Possibly protocol version |
| 0x04 | `0x00` + 3 zero bytes | 4 bytes, per-model | VGS: `01 02 04 06`; Display sub-device: `01 02 08 06`. Byte 2 may encode device type (0x04=wheel, 0x08=display) |
| 0x05 | `0x00` + 3 zero bytes | 4 bytes, per-model | Capability flags? VGS: `01 02 1f 01`; CS V2.1: `01 02 26 00`; Display: `01 02 00 00` |
| 0x06 | — (n=0) | 12 bytes | Hardware identifier. VGS: `be 49 30 02 14 71 35 04 30 30 33 37` |
| 0x07 | `0x01` | 16-byte string | **Model name** — `VGS`, `CS V2.1` |
| 0x08 | `0x01` | 16-byte string | **HW version** — `RS21-W08-HW SM-C` |
| 0x08 | `0x02` | 16-byte string | **HW revision** — `U-V12`, `U-V02` |
| 0x0F | `0x01` | 16-byte string | **FW version** — `RS21-W08-MC SW` |
| 0x10 | `0x00` | 16-byte string | **Serial number, first half** |
| 0x10 | `0x01` | 16-byte string | **Serial number, second half** |
| 0x11 | `0x04` | 2 bytes | Unknown |

Full serial = two halves concatenated (32 ASCII chars).

### Display sub-device response table (wrapped in 0x43)

Display sub-device identity responses are routed through the main wheel's 0x43 group. The **wrapped response** arrives as `0xC3 0x71 [inner_response_byte] [inner_payload...]` where the inner byte is the toggled-group response of the original identity probe (0x02 → 0x82, 0x04 → 0x84, etc.). Parser must unwrap the outer 0x43/C3 frame and then decode the inner response as if it were a top-level identity reply.

Observed wrapped responses (from live sim capture, 2026-04-22; matches `docs/moza-protocol.md` § Wheel connection probe sequence inner shapes):

| Inner response | Example payload | Meaning |
|----------------|-----------------|---------|
| `0x89 00 01` | presence reply | sub-device count = 1 |
| `0x82 02` | product type = 2 | |
| `0x84 01 02 08 06` | device type reply | byte 2 = `0x08` = display |
| `0x85 01 02 00 00` | capabilities | display has no caps |
| `0x86 <12B>` | hardware ID | 12-byte STM32 MCU UID for the display controller |
| `0x87 0x01 "<ASCII>"` | model name | `"Display"` |
| `0x88 0x01 "<ASCII>"` | HW version | e.g. `RS21-W08-HW SM-D` |
| `0x8F 0x01 "<ASCII>"` | FW version | e.g. `RS21-W08-HW SM-D` |
| `0x90 0x00 "<ASCII>"` | serial number | |
| `0x91 0x04 0x01` | identity-11 | |

Plugin mapping: `MozaResponseParser.ParseDisplayIdentity()` decodes each inner response and returns a `ParsedResponse` with a `display-*` command name (`display-model-name`, `display-hw-version`, etc.). `MozaData` stores them in `Display*` fields distinct from the base wheel's identity fields.

### Display sub-device (inside VGS wheel)

During dashboard upload, Pithouse runs same probe against **Display** sub-module inside wheel (routed via `0x43` frames). Distinct identity:

| Field | VGS (wheel) | Display (sub-module) |
|-------|-------------|---------------------|
| Model (0x07) | `VGS` | `Display` |
| HW version (0x08/01) | `RS21-W08-HW SM-C` | `RS21-W08-HW SM-D` |
| HW revision (0x08/02) | `U-V12` | `U-V14` |
| Caps (0x05) | `01 02 1f 01` | `01 02 00 00` |
| Type (0x04) byte 2 | `04` | `08` |
| Serial | (differs) | (differs) |

SM-C/SM-D suffix distinguishes main controller from display controller. Display has no capability flags.

**Timing:** Pithouse probes Display at ~t=9.97s — AFTER telemetry starts (t=9.88). Not a prerequisite for telemetry.

**Plugin probe sequence** (from `moza-startup.json` 2026-04-12):

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

Plugin sends steps 1-10 during preamble. `0x87` response with model "Display" sets `DisplayDetected=true`, gates dashboard telemetry features in UI — wheels without screen (e.g. CS V2.1) won't respond.

### Known wheel model names

Confirmed from USB captures + live serial queries:

| Model name | Wheel | Source |
|------------|-------|--------|
| `VGS` | Vision GS | USB capture (`cs-to-vgs-wheel.ndjson`). 8 button LEDs, no flag LEDs |
| `CS V2.1` | CS V2 | USB capture (`vgs-to-cs-wheel.ndjson`) |

Assumed from device naming conventions (unverified):

| Prefix | Wheel | Notes |
|--------|-------|-------|
| `GS V2P` | GS V2P | 10 button LEDs (5 per side), no flag LEDs |
| `W17` | CS Pro | 18 RPM LEDs, no flag LEDs (firmware reports `W17`) |
| `W18` | KS Pro | 18 RPM LEDs, no flag LEDs (firmware reports `W18`) |
| `KS` | KS | 10 button LEDs, no flag LEDs |
| `FSR2` | FSR V2 | Has flag LEDs |
| `TSW` | TSW | 14 button LEDs, no flag LEDs |

### ES wheel identity caveat

ES (old-protocol) wheels share device ID `0x13` with wheelbase. Identity queries sent to `0x13` return **base** identity, not wheel. Example: ES wheel on R5 base returns `R5 Black # MOT-1` (base identity). No known way to query ES wheel's own model name through serial protocol.

---

## Heartbeat and keepalives

- **Group 0x00 heartbeat** — sent to every known device ID (18–30) ~1/s. Payload length 0. Keep-alive / presence check.
- **Group 0x43 bare keepalive** — bare `0x43` frames (n=1, payload=`0x00`) to devices 0x17/0x14/0x15 every ~1.1s. Device replies `0x80`. Connection-level ping.
- **Group 0x43 broadcast** — length=2 packets to dash (0x14) and device 0x15 every ~5s. Heartbeat/sync.

### Unsolicited messages

- **Group 0x0E** from wheel (device 23): ASCII debug/log text, ~every 2s. NRF radio stats, e.g. `NRFloss[avg:0.00000%] recvGap[avg:4.70100ms]`.
- **Group 0x06** from wheel (device 23): 12-byte hardware identifier. In `connect-wheel-start-game.json` host-initiated (part of probe), not purely unsolicited. VGS response: `be 49 30 02 14 71 35 04 30 30 33 37`.

---

## Telemetry channel encoding

Master reference for all compression types.

| Type | Bits | TierDef code | Encode (game → raw) | Decode (raw → game) | Range / note | Count in Telemetry.json |
|------|------|--------------|---------------------|---------------------|--------------|-------------------------|
| `bool` | 1 | 0x00 ✓ | `raw = value` | `value = raw` | 0 or 1 | 51 |
| `uint3` | 4 | 0x14 ✓ | `raw = min(value, 15)` | raw, 15=N/A | 0–14 | 1 |
| `uint8` | 4 | inferred | `raw = min(value, 15)` | raw, 15=N/A | 0–14 | 5 |
| `uint15` | 4 | 0x03 inferred | `raw = min(value, 15)` | raw, 15=N/A | 0–14 | 1 |
| `int30` | 5 | 0x0D ✓ | `raw = min(value, 31)` | raw, -1=R as 31 | Gear (-1=R, 0=N, 1–12) | 1 |
| `uint30` | 5 | inferred | `raw = min(value, 31)` | raw | 0–31 | 2 |
| `uint31` | 5 | inferred | `raw = min(value, 31)` | raw | 0–31 | 1 |
| `int8_t` | 8 | 0x02 inferred | `raw = value` | raw | signed byte | — |
| `uint8_t` | 8 | 0x01 inferred | `raw = value` | raw | 0–255 | 12 |
| `percent_1` | 10 | 0x0E ✓ | `clamp(game% × 10, 0, 1000)` | `game% = raw / 10` | 0–100%, 1023=N/A | 19 |
| `float_001` | 10 | 0x17 ✓ | `clamp(game × 1000, 0, 1000)` | `game = raw / 1000` | 0.0–1.0, 1023=N/A | 3 |
| `tyre_pressure_1` | 12 | 0x10 inferred | `clamp(kPa × 10, 0, 4095)` | `kPa = raw × 0.1` | 0–409.5 kPa | 12 |
| `tyre_temp_1` | 14 | 0x11 inferred | `°C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C | 43 |
| `track_temp_1` | 14 | 0x12 inferred | `°C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C | 5 |
| `oil_pressure_1` | 14 | 0x13 inferred | `°C × 10 + 5000` | `°C = (raw − 5000) × 0.1` | −500–1138.3°C | 1 |
| `int16_t` | 16 | 0x05 inferred | `raw = value` | raw | signed 16 | — |
| `uint16_t` | 16 | 0x04 ✓ | `raw = value` | raw | 0–65535 | 2 |
| `float_6000_1` | 16 | 0x0F ✓ | `clamp(game × 10, 0, 65535)` | `game = raw / 10` | 0–6553.5 | 4 |
| `float_600_2` | 16 | 0x15 inferred | `clamp(game × 100, 0, 65535)` | `game = raw / 100` | 0–655.35 | 12 |
| `brake_temp_1` | 16 | 0x16 inferred | `clamp(°C × 10 + 5000, 0, 65535)` | `°C = (raw − 5000) / 10` | −500–6053.5°C | 14 |
| `uint24_t` | 24 | — | `raw = value` | raw | 0–16777215 | — |
| `float` | 32 | 0x07 ✓ | IEEE 754 single bits | IEEE 754 reinterpret | full float | 73 |
| `int32_t` | 32 | 0x08 inferred | `raw = value` | raw | signed 32 | 3 |
| `uint32_t` | 32 | 0x09 inferred | `raw = value` | raw | 0–2³²-1 | 65 |
| `double` | 64 | 0x0A inferred | IEEE 754 double bits | IEEE 754 reinterpret | full double | — |
| `location_t` | 64 | 0x0B inferred | IEEE 754 double bits | IEEE 754 reinterpret | track coords | 65 |
| `int64_t` / `uint64_t` | 64 | — | raw | raw | 64-bit | — |
| `string` | var | — | — | — | names | 15 |

✓ = confirmed from F1 dashboard USB capture. Inferred codes assigned sequentially by factory ID order from Telemetry.json. Code 0x06 unassigned (gap between int16_t and float).

**Notes:**
- `DoubleInterface` flag byte at object offset +4: flag=1 returns 32-bit (`float`), flag=0 returns 64-bit (`double`).
- Factory ID 20 (`uint3`, `uint8`, `uint15`) maps through abstract `IsUnsignedInterface` → `Int15Interface` (4 bits). Type name's number does NOT determine bit width.
- `UFloatInterface` reads per-instance exponent from `this+8`. Scale = `10^exponent`. Type name encodes `float_{max}_{decimal_places}`: `float_6000_1` = max ~6000, 1 decimal.
- CSP uses tier-def version 0 (URL-based) which doesn't need compression codes — wheel firmware resolves by URL.

### Key constants

| Value | Usage |
|-------|-------|
| 10.0 | Scale for percent, UFloat, temps, pressures (×10) |
| 100.0 | Normalized → percent (×100 then ×10) |
| 1000.0 | Max raw for 10-bit percent/normalized |
| 5000.0 | Temperature offset (raw = temp×10 + 5000) |
| 65535.0 | Max raw for 16-bit UFloat/BrakeTemp |

### Channel ordering

Channels first grouped by `package_level` (30 → base frame, 500 → base+1, 2000 → base+2). Within each frame packed **alphabetically by URL suffix** (part after `v1/gameData/`). Iterated sorted by URL, packed sequentially into bit stream starting at bit 0.

Bits packed **LSB-first within each byte** (bit 0 = LSB of byte 0, bit 8 = LSB of byte 1). Multi-bit fields span byte boundaries when needed.

### Namespace distribution (Telemetry.json, 410 total channels)

| Namespace | Count | Notes |
|-----------|-------|-------|
| `v1/gameData/` | 275 | Standard game telemetry |
| `v1/gameData/patch/` | 133 | Extended: 64 track map coords, 64 race info slots, display names |
| `v1/preset/` | 2 | `CurrentTorque`, `SteeringWheelAngle` (both `float_6000_1`, 16 bits) — wheelbase state, NOT game telemetry |

---

## ServiceParameter value transforms (rs21_parameter.db)

`ServiceParameter` table documents how raw **device setting** values (groups 31–100) map to display units. Separate from telemetry encoding above — applies to Pit House settings UI, NOT telemetry bit stream.

| Function | Params | Example | Meaning |
|----------|--------|---------|---------|
| `multiply` | `0.01` | FFB strength 0–10000 → 0–100% | Raw × 0.01 |
| `multiply` | `0.1` | Temperature raw → degrees | Raw × 0.1 |
| `multiply` | `0.05` | Step values | Raw × 0.05 |
| `multiply` | `2` | Some parameters | Raw × 2 |
| `division` | `65535` | Normalize 16-bit | Raw / 65535 → 0.0–1.0 |
| `division` | `16384` | Normalize 14-bit | Raw / 16384 → 0.0–1.0 |
| `softLimitStiffness_conversion` | — | Soft limit stiffness | Custom non-linear |

---

## Live telemetry stream (group 0x43, device 0x17, cmd `[0x7D, 0x23]`)

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

### Flag byte and multi-stream architecture

Pit House sends telemetry as **three concurrent streams** using different flag bytes, one per `package_level` tier defined in `GameConfigs/Telemetry.json`. Each stream carries channels assigned to its tier, bit-packed alphabetically by URL suffix.

| Flag offset | `package_level` | Update rate | Content |
|-------------|----------------|-------------|---------|
| base (e.g. `0x0a`, `0x13`) | 30 | ~30 ms | Channels with `package_level: 30` |
| base+1 | 500 | ~500 ms | Channels with `package_level: 500` |
| base+2 | 2000 | ~2000 ms | Channels with `package_level: 2000` |

`package_level` is authoritative routing key — channel's tier fixed in `Telemetry.json`, independent of active dashboard. If tier has no active channels, frame sent as 2-byte stub `[flag][0x20]`. Flag value is monotonic counter assigned per connection; base+1 and base+2 always exactly one and two above base.

### Flag byte values across captures

Wheel accepts flags at 0x00, 0x02, 0x07, 0x0a, 0x13 — any value works as long as tier definition and telemetry frames agree. Exact relationship between enable entry offsets and tier flag bytes is **not fully understood**. Plugin exposes `FlagByteMode` (0=zero-based, 1=session-port-based, 2=two-batch) for empirical testing.

**Pithouse flag byte assignment (confirmed 2026-04-12 comparative captures):** Pithouse **always** uses 0-based flag bytes regardless of session port. In both `moza-startup-1` and `moza-startup-2`, tier definitions use flags 0x00, 0x01, 0x02 and first telemetry frame uses flag=0x00 — even though telemetry session was on port 0x02. Pithouse starts with flag=0x00 (fastest tier) and sends all tier flags from first frame.

Observed flag bytes (from raw JSON):

| Capture | Flag |
|---------|------|
| `moza-startup.json` | 0x02 — first port after power-on |
| `burn-tyres.json` | 0x0a — later connection |
| `0-100redline-0-main-dash.json` | 0x13 — even later connection |

### Example: F1 dashboard tier layouts

Level-30 channels (base frame), alphabetical, verified from capture (Gear at bit 79):

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

Total payload bytes = `ceil(sum_of_channel_bit_widths / 8)`. 128 bits = 16 bytes.

Level-2000 frame (base+2) — 6 channels, 104 bits = 13 bytes exactly:

| Bits | Channel | Compression | Width |
|------|---------|-------------|-------|
| 0–31 | BestLapTime | `float` | 32 |
| 32–63 | LastLapTime | `float` | 32 |
| 64–73 | TyreWearFrontLeft | `percent_1` | 10 |
| 74–83 | TyreWearFrontRight | `percent_1` | 10 |
| 84–93 | TyreWearRearLeft | `percent_1` | 10 |
| 94–103 | TyreWearRearRight | `percent_1` | 10 |

All 3 F1 tiers verified byte-size-match vs Pithouse: Level 30 = 16B, Level 500 (FuelRemainder only) = 2B, Level 2000 = 13B.

### Data verification (2026-04-12)

Byte-level verification complete:
- Header `7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data] [checksum]` — constant bytes, N, checksum match Pithouse exactly.
- LSB-first `TelemetryBitWriter` correct. Case-insensitive URL sort matches Pithouse.
- Encoding formulas verified against capture: `float_001` (×1000), `percent_1` (×10), `uint16_t` (direct), `float_6000_1` (×10), `int30` (5-bit, -1→31), `float` (IEEE 754), `bool` (0/1).

---

## Telemetry control signals

### Dash telemetry enable (group 0x41, device 0x17, cmd `[0xFD, 0xDE]`)

Sent ~100×/s. Data always `00 00 00 00`. Likely mode/enable flag — value 0 = telemetry active.

### Sequence counter (group 0x2D, device 0x13, ~50 Hz)

Cmd `[0xF5, 0x31]`. Data: `00 00 00 XX` where XX increments by 1 each send. Base unit sequence counter.

### RPM LED telemetry (group 0x3F, device 0x17, cmd `[0x1A, 0x00]`)

Sent ~once/s. 8 data bytes = 4 × 16-bit LE values:

```
[current_pos, 0x0000, 0x03FF, 0x0000]
```

- `current_pos = current_rpm / max_rpm × 1023` — 10-bit RPM fraction
- Value 3 always 1023 (fixed denominator)
- Values 2 and 4 always 0

### LED group colour (group 0x3F, device 0x17, cmd `[0x27, <group>, <role>]`)

Sets the **idle** and **active** colours for an entire LED group on new-protocol
wheels. Wire frame (6-byte body + checksum):

```
7E 06 3F 17 27 <group> <role> <R> <G> <B> <chk>
```

- `group` — LED group selector:
  - `0x00` — RPM strip (central LED bar)
  - `0x01..0x05` — rotary knobs 1..5 (CS Pro has 4 knobs, KS Pro has 5). Group
    indices beyond the physical knob count are silently ignored by firmware.
- `role` — colour role:
  - `0x00` — background / idle (colour shown while the knob is stationary or
    the RPM bar is unlit)
  - `0x01` — primary / active (colour flashed on rotation, or used as the lit
    RPM colour when telemetry isn't driving the bar)
- `R G B` — 24-bit RGB, 0x00..0xFF each channel.

Captured examples (CS Pro, W17):

```
7E 06 3F 17 27 01 00 FF 00 00 0E   # knob 1 background = red
7E 06 3F 17 27 01 01 FF FF FF 0D   # knob 1 primary   = white
7E 06 3F 17 27 03 00 00 FF 00 10   # knob 3 background = green
7E 06 3F 17 27 03 01 FF 00 00 11   # knob 3 primary   = red
```

Wheel echoes `(group | 0x80)` / swapped device nibble / payload mirror — plugin
recognizes via `WheelEchoPrefixes` entries for `(0x3F, 0x17, 0x27, 0x00..0x05)`.
Not readable — the `0x27 <group> 0xFF` form reads *brightness* for the same
group, not colour. Plugin persists the last-written values in
`MozaPluginSettings.WheelKnobBackgroundColors` / `WheelKnobPrimaryColors` (and
the matching fields on `MozaWheelExtensionSettings` / `MozaProfile`) and
re-pushes them on wheel detect.

Command names in `MozaCommandDatabase`: `wheel-knob{1..5}-bg-color`,
`wheel-knob{1..5}-primary-color` (3-byte array payload = RGB).

---

## Tier definition protocol (group 0x43, session data on 7c:00)

Tier configuration uses TLV (tag-length-value) encoding exchanged as 7c:00 session data chunks. **Two-way handshake**: wheel declares channel catalog, host tells wheel how to decode incoming telemetry.

### Handshake sequence (from bidirectional frame traces)

Before Pithouse opens sessions, wheel already advertises channel catalog via `7c:23` display config frames. Full handshake traced frame-by-frame from VGS (`moza-startup-1.pcapng`) and CSP (`pithouse-complete.txt`):

```
Phase 1 — Wheel advertisement (before session opens):
  Wheel sends 7c:23 display config frames at ~10Hz (alternating payloads)

Phase 2 — Session open + wheel channel catalog:
  Host  >>> 7C:00 SESSION_OPEN port=0x01, port=0x02 (same USB packet)
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

Both VGS and CSP follow this sequence. Wheel always declares version 0 (`tag 0x07 param=1 value=0x00`) — both models send identical version tags. Pithouse decides host→wheel response format based on wheel's model name (from 0x87 identity response), not from version tag.

**Timing note:** On VGS, Pithouse starts telemetry (flag=0x00, 11B probe tier) at t+0.3s after session open, BEFORE enable signal or channel config. Enable starts at t+1.0s. Real dashboard telemetry (flag=0x03, 16B) starts at t+1.5s after second tier definition batch.

### Session 0x01 — device description (both directions, both models)

Wheel and Pithouse send short descriptor on session 0x01. Structure identical:

```
[0x07] [01 00 00 00] [00]                     — version 0
[0x0c] [size] [data...]                        — device-specific hash/fingerprint
[0x01] [size: u32 LE] [data...]               — descriptor body
[0x05] [00]                                    — unknown
[0x04] [size] [ch_index=0] [url or padding]   — single channel entry (index 0)
[0x06] [00]                                    — end
```

Tag 0x0c (14 bytes) differs per device — VGS: `0c 06 69 42 07 14 e8 06...`, CSP: `0c 04 8a e5 d0 86 b2 fc...`. May encode hardware ID or firmware fingerprint. Channel entry at index 0 appears to be padding (3 ASCII spaces on VGS).

### Session 0x02 — channel catalog (wheel → host, both models)

Wheel sends supported channels. Identical structure VGS and CSP:

```
[0xff]                                         — sentinel / reset marker
[0x03] [04 00 00 00] [01 00 00 00]            — config param (value=1, constant)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

VGS reports 16 channels (BestLapTime, Brake, CurrentLapTime, DrsState, ErsState, FuelRemainder, GAP, Gear, LastLapTime, Rpm, SpeedKmh, Throttle, TyreWear×4). CSP reports 20 channels (adds ABSActive, ABSLevel, TCActive, TCLevel, TyrePressure×4, TyreTemp×4).

Catalog tells host what currently loaded dashboard subscribes to. Channel indices 1-based, sorted alphabetically by URL.

### Session 0x02 — host response: version 0 URL subscription (CSP)

For CSP, Pithouse responds on session 0x02 with same tag 0x04 format — echoing back channel URLs as subscription confirmation. Wheel firmware knows compression types internally.

```
[0xff]                                         — sentinel / reset
[0x03] [04 00 00 00] [01 00 00 00]            — config (value=1)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel subscription (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

Pithouse sends twice in rapid succession (first immediately after session open, then again after acks arrive). Confirmed from `CSP captures/pithouse-complete.txt` (20 channels, identical to wheel catalog).

### Session 0x02 — host response: version 2 compact tier definitions (VGS)

Pithouse sends different format: flag bytes, channel indices, compression codes, bit widths. Wheel told exactly how to decode bit stream.

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

Optionally followed by enable entries and second batch:
```
[0x00] [01 00 00 00] [flag_offset]           — tier enable (repeated per tier)
[0x01] ...                                    — second batch at higher flag values
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker with actual count
```

Pithouse sends two batches: "probe" batch at flags 0x00+ with `total_channels=0`, then "real" batch at higher flags with actual dashboard channels and total count. Wheel accepts telemetry on flags from either batch.

**Channel indices** 1-based, assigned alphabetically by URL across all tiers (not per-tier).

Compression codes: see master table in § Telemetry channel encoding.

### Tag 0x03 — config parameter

Tag 0x03 has different values depending on direction and version:

| Direction | Version | Value | Interpretation |
|-----------|---------|-------|---------------|
| Wheel → Host | 0 | 1 | Constant across VGS and CSP |
| Host → Wheel | 0 (CSP) | 1 | Mirrors wheel value |
| Host → Wheel | 2 (VGS) | 0 | Different meaning in version 2 context |

### Chunking (both versions, both directions)

All 7c:00 session data uses SerialStream chunks with CRC-32 trailers (standard ISO 3309). **ALL chunks have CRC-32 trailers, including final chunk** — verified by computing CRC-32 of every chunk's net data across multiple captures. Max 54 net bytes per chunk (58 with CRC).

---

## SerialStream session protocol (group 0x43, cmd `7c:00` / `fc:00`)

Pit House transfers dashboard files, tier definitions, and RPCs using proprietary TCP-like serial stream protocol (`MOZA::Protocol::SerialStreamManager`) over `0x43/7c:00`. `fc:00` used for acknowledgments. NOT CoAP — CoAP is separate layer for device parameter management.

### Chunk format

Each `7c:00` data field contains one chunk:

```
session(1)  type(1)  seq_lo(1)  seq_hi(1)  payload(≤58)
```

| Field | Size | Description |
|-------|------|-------------|
| session | 1 | Session ID — pre-assigned, multiple concurrent |
| type | 1 | `0x01` = data, `0x00` = control/end marker, `0x81` = session channel open (device-initiated) |
| seq | 2 LE | Sequence number (monotonic within session) |
| payload | ≤58 | Net data per chunk; **all data chunks have 4-byte CRC-32 trailer** |

Net payload per full data chunk: **54 bytes** (58 minus 4-byte CRC). All data chunks include CRC-32 trailer, including final chunk.

### CRC algorithm

**Standard CRC-32** (ISO 3309 / ITU-T V.42, same as zlib/Ethernet/gzip/PNG):
- Polynomial: `0x04C11DB7` (reflected), init `0xFFFFFFFF`, xor-out `0xFFFFFFFF`
- Stored **little-endian** in 4-byte trailer
- Covers only **54-byte payload data** (excludes session/type/seq header)
- Per-chunk (not cumulative)
- Computable via `zlib.crc32(payload_bytes)` or `System.IO.Hashing.Crc32`

### Acknowledgments

`fc:00` with 3 bytes: `session(1) + ack_seq(2 LE)`. Session ID in ack identifies **ack sender's** session, not data sender's. Linked session pairs (e.g. 0x03↔0x0A) use cross-session acks.

**Session-open ACK must echo host's open_seq.** When host sends type=0x81 session open with `seq_lo:seq_hi`, wheel's `fc:00` ack must carry same seq value. Pithouse maintains monotonic port counter incrementing on each disconnect/reconnect; if wheel always replies with `ack_seq=0`, Pithouse treats as stale and retries endlessly (observed: 552 retries over 2.5 minutes). Counter starts at 1 on first power-on but increments across sessions.

### Session open frames

**Host-initiated (type=0x81, 4-byte payload):**
```
7E 0A 43 17 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [checksum]
                   └─chunk ID   └─seq(LE)=port       └─session_id(LE)   └─window=765
```

Pithouse opens **two sessions simultaneously** (0x01 and 0x02) in same USB packet. Wheel responds with `fc:00` acks for both. The `fc:00` session bytes in steady state track **session ack protocol** (incrementing ack_seq for each 7c:00 data chunk received), NOT telemetry flag byte.

**Device-initiated (type=0x81, 6-byte payload):**

Device opens sessions 0x04, 0x06, 0x08, 0x09, 0x0A with 6-byte form (not 4-byte host form):

```
7E 0A C3 71 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [cksum]
```

Port field duplicated (observed every device-initiated open across 4 captures). `port` equals session byte for every device-opened session (0x04→4, 0x06→6, 0x08→8, 0x09→9, 0x0A→10). `FD 02` trailer constant.

### Session close frame

Type=0x00 end marker: **6-byte payload**: `7C 00 [session] 00 [ack_lo] [ack_hi]` (ack_seq may be zero when reclaiming stale session). Length byte must equal 6. A 4-byte payload advertised as length 6 causes wheel (and `sim/wheel_sim.py`) to over-read into next frame and de-sync.

### Port / session-byte allocation

**2026-04 firmware (old):** global monotonic counter shared between host and wheel. Host picks low numbers (1, 2, 3...), wheel picks its own (6, 8, 9...). Next host allocation accounts for wheel-allocated ports. Counter resets on wheel power cycle.

Observed session opens in `moza-startup.json` (2026-04-12):

| Time | Source | Session byte | Port (payload) | Notes |
|------|--------|-------------|----------------|-------|
| 8.756s | Host | 0x01 | 0x0001 | First host session (mgmt/upload) |
| 8.756s | Host | 0x02 | 0x0002 | Second host session (telemetry config) |
| 11.102s | Wheel | 0x08 | 0x0008 | Wheel-initiated keepalive |
| 11.102s | Wheel | 0x09 | 0x0009 | Wheel-initiated configJson RPC |
| 11.187s | Host | 0x03 | 0x000a | Third host session — port 10, not 3! |
| 11.894s | Wheel | 0x06 | 0x0006 | Wheel-initiated keepalive |

**Session byte** (chunk header) and **port number** (payload) different for session 0x03 — session byte is host-local identifier, port is globally allocated.

**2025-11 firmware:** global counter observation **no longer holds**. From `automobilista2-wheel-connect-dash-change.pcapng`: host opens session 0x03 with port 0x0003 (not 0x000a as in 2026-04). Session byte and port now match for every session, both sides. Device-opened sessions 0x04/0x06/0x08/0x09/0x0A all use `port == session`. Implementations should not assume wheel-side port allocation; use `port == session` for everything.

### Concurrent session map

Up to 9 concurrent sessions during dashboard management. Confirmed across 4 captures (moza-startup, connect-wheel-start-game, moza-unplug-plug-wheel-to-base, automobilista2-wheel-connect-dash-change):

| Session | Opened by | Role | Description |
|---------|-----------|------|-------------|
| 0x01 | **host** | Management | Wheel identity / log push; `0xFF`-prefixed messages |
| 0x02 | **host** | Telemetry | Tier definition, FF-prefixed settings push |
| 0x03 | **host** | Aux config | Tile-server / settings push (zlib-compressed) |
| 0x04 | **device** | **File transfer** | Bidirectional: host uploads `.mzdash`; device sends root directory listing |
| 0x06 | device | Keepalive | Alternating directions, ~3.4s |
| 0x08 | device | Keepalive | Alternating directions, ~3.4s |
| 0x09 | device | **configJson RPC** | Device pushes dashboard state; host responds with canonical list |
| 0x0A | device | Keepalive / RPC | Dev→host, ~3.4s; also host-initiated RPC calls |

**Opening order** (cold-start captures):
1. Host opens 0x01, 0x02 (mgmt + telemetry) within ~1 ms of each other (t=0).
2. Host opens 0x03 ~150–450 ms later (port 0x03 new firmware; port 0x0a older).
3. Device opens 0x04, 0x06 ~40–400 ms after host 0x02.
4. Device opens 0x08, 0x09 ~1.5–2.5 s later (retransmitted every 1 s up to 3 tries until host ACKs).
5. Device opens 0x0A last, variably (t=38s or later).

**Sessions 0x08 and 0x09 are retransmitted** until host sends `fc:00` ack. Real wheel sends each up to 3 times at 1 s intervals. Sim implementations should do same if host doesn't ACK immediately.

### Compressed transfer format (sessions 0x09, 0x0a)

Sessions 0x09 (configJson state) and 0x0a (RPC) prepend a 9-byte header to the reassembled application data:

```
flags(1)  comp_sz+4(4 LE)  uncomp_sz(4 LE)  [zlib data...]
```

The `comp_sz` field stores the compressed byte count **plus 4** (confirmed across five reset-RPC blobs in 2026-04-21 captures — envelope `00 1d 00 00 00 11 00 00 00` for a 25-byte zlib stream and 17-byte JSON body, i.e. field=29, comp=25, uncomp=17). Zlib stream uses standard deflate (`78 9c` magic). Reassembly: strip 4-byte CRC from each chunk, concatenate payloads (excluding session/type/seq headers), then parse 9-byte header and decompress.

**Session 0x04 root directory listing does NOT use this envelope** — it uses a 53-byte prefix documented in § Session 0x04 device → host root directory listing. Session 0x03 tile-server uses a third format (12-byte wrapper, § below). **One envelope per session**, not shared.

### Session 0x03 tile-server envelope (variant, 12 bytes)

**Session 0x03 uses a different 12-byte envelope format**, reversed from live PitHouse captures (2026-04-21):

```
FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE]
```

| Bytes | Field | Notes |
|-------|-------|-------|
| 0 | `0xFF` marker | Constant — same sentinel used for session 0x01/0x04 FF-prefixed fields |
| 1 | `0x01` sub-msg index | Constant |
| 2 | `0x00` tag | Constant |
| 3..6 | compressed_size + 4 (u32 LE) | Observed: `FB 00 00 00` (=251, for 247-byte zlib) and `91 04 00 00` (=1169, for 1165-byte zlib). The `+4` likely accounts for the zlib stream's Adler-32 trailer |
| 7 | `0xFF` separator | Constant |
| 8 | `0x00` tag | Constant |
| 9..11 | uncompressed_size (u24 **BE**) | Big-endian unlike other sizes. Observed: `00 03 07` (=775) and `00 18 9D` (=6301) |

Only used for tile-server map metadata JSON blobs (`{"map":{"ats":"...","ets2":"..."},"root":"...","version":N}`). Plugin helper: `Telemetry/TileServerStateBuilder.BuildEnvelope()`.

### Type 0x81 — session channel open payload

Device sends type `0x81` to initiate or acknowledge session. Payload 4 bytes:

```
session_id(2 LE)  receive_window(2 LE)
```

Observed: `04 00 fd 02` → session 4, window 765.

### Session 0x0a RPC (host → device)

Plugin exposes `TelemetrySender.SendRpcCall(method, arg, timeoutMs)` to send JSON RPCs on session 0x0a in same 9-byte `[flag=0x00][comp_size+4:u32 LE][uncomp_size:u32 LE][zlib]` envelope used by `configJson` on session 0x09. Request shape `{"<method>()": <arg>, "id": <N>}`. Reply shape **mirrors the request**: `{"<method>()": <return>, "id": <same N>}` — NOT `{"id": N, "result": ...}`. The `<return>` value is an empty string for the reset RPC (only shape confirmed by pcap); sim uses `""` for every reply pending a capture of a real wheel's `completelyRemove` reply. Replies routed by `id` via dictionary of waiters so multiple in-flight RPCs tracked concurrently.

**Cross-check (2026-04-22):** earlier sim emitted `{"id": N, "result": ...}` replies and PitHouse silently dropped them — the Dashboard Manager stayed stuck on the pre-delete state and refused to initiate any subsequent upload. Switching to the mirrored-key reply shape cleared the stall on the sim side (delete RPC round-trip confirmed end-to-end).

Known methods (observed via pithouse sim capture, 2026-04-21):

| UI action | Request | Notes |
|-----------|---------|-------|
| Delete dashboard | `{"completelyRemove()": "{<uuid>}", "id": N}` | UUID in Microsoft GUID format e.g. `{7c218515-6ec6-4e5f-9820-ba030b14c43d}`. **The `<uuid>` is PitHouse's own per-install cache key, NOT the id the sim advertised in `enableManager.dashboards[].id`.** Observed uuids include all-zero placeholders like `{00000000-0000-0000-0000-000000000003}` and random 32-char strings (`gLib1v4iWa5XZBCDew8R71yImlYyyaBC`). Sim-side delete handlers must fall back to dirName/hash/title matching (and a single-non-factory-dashboard heuristic when FS holds exactly one user upload) because the uuid will never match whatever the sim reports on session 0x09. |
| Reset dashboard | `{"()": "", "id": N}` | **Empty method name** (literal `()`), empty args |

**Observed id semantics (PitHouse sim, 2026-04-21):** id is **NOT a monotonic RPC counter**. Across 4 rapid consecutive "Reset Dashboard" clicks within one Pithouse session, all 4 frames carried `id=13`. A prior Pithouse session used `id=15` for a single reset. Id appears to be a **session-scoped target reference** — assigned by Pithouse at connect time, reused for all calls targeting the same item. Different connect = different id. Practical implication: wheel sim / plugin should accept any integer id and echo it back in reply, not expect sequential ids.

---

## Dashboard upload protocol

### OPEN QUESTION — which upload path is current?

Two upload structures described across captures. Not yet confirmed whether these are:
- (a) Two different upload paths firmware supports in parallel
- (b) Same path described from different capture/understanding eras (one stale)
- (c) Different firmware versions (2026-04 vs 2025-11)

Both documented below; **plugin currently implements Path B** (session 0x04, sub-msg 1/2 — matches 2025-11 firmware). Path A code path was removed when 2025-11 firmware shipped and only recognised Path B. Needs investigation whether older firmware still requires Path A in parallel.

### Path A — session 0x01 host-initiated FF-prefix upload (plugin implementation)

Confirmed by CRC-32 verification across VGS and CSP captures. Each sub-message:

```
[FF] [payload_size: u32 LE] [payload bytes]
[remaining_transfer_size: u32 LE]
[CRC32: u32 LE]                              ← covers ALL preceding bytes from FF through remaining_size
```

Three sub-messages sent:

| Field | Payload size | Content | Notes |
|-------|-------------|---------|-------|
| 0 | 16 bytes | Device tokens (session-specific, differs per wheel) | remaining = total size of fields 1+2 |
| 1 | 8 bytes | `9e 79 52 7d 07 00 00 00` — protocol constant | Identical between VGS and CSP. remaining=3. NOT a literal in PE binary (computed/serialized at runtime) |
| 2 | varies (VGS: 1350, CSP: 100) | Compressed mzdash content | 12B pre-header + zlib stream (last field, no remaining/CRC trailer) |

Each field except last followed by `remaining_transfer_size(4 LE) + CRC32(4)`. CRC covers all bytes from `FF` through `remaining_transfer_size`. Field 2 is last, no trailing remaining/CRC.

**Field 0 tokens** (16 bytes = two 8-byte LE values):
- Token 1 = `[random_u32 | 0x00000002]`
- Token 2 = `[unix_timestamp | 0x00000000]`

Confirmed from 8 sessions across VGS and CSP: token 2 always Unix timestamp of session start; token 1 high 32 bits always `0x00000002` (protocol version or request type); token 1 low 32 bits CSPRNG output (no deterministic relationship to timestamp — tested CRC-32, FNV-1a, DJB2, MurmurHash3, mt19937, 12 LCG variants, crypto hashes, all negative). Correlation IDs, not validated by wheel. Pithouse's `Sync_DashboardManager` uses `mcUid` (STM32 MCU hardware UID, via `MainMcuUidCommand`) as per-device routing key, but mcUid NOT encoded in upload tokens.

**Field 0 remaining semantics:** Field 0 remaining = total bytes of subsequent fields (field 1 block + field 2 block). Value `7200` observed corresponds to dashboards like "Formula Racing V1-Mission R" (7170B compressed + 38B framing = 7208). Verified by computing zlib-compressed sizes of all 47 Pithouse dashboards — formula `38 + compressed_size` matches captures. `0x1C20` NOT hardcoded constant. Field 1 remaining = `3` in all captures — NOT byte count (field 2 much larger). Semantics unknown; possibly field count or message type constant.

**Field 2 pre-zlib header** (12 bytes before `78 da` zlib magic):
```
[CRC32_or_hash: 4B] [08 00 00 00: constant] [uncompressed_size_BE: 4B]
```

Zlib-compressed content IS mzdash dashboard file — confirmed by partial decompression producing UTF-16LE channel names (`RpmAbsolute1`, etc.).

**Pithouse re-uploads dashboard on every connection** — confirmed in `moza-unplug-plug-wheel-to-base.pcapng` (VGS) and `CSP captures/pithouse-complete.txt` (CSP). Pithouse does not check what's already loaded — always pushes from internal state. May be prerequisite for telemetry.

### Path B — session 0x04 device-initiated sub-msg 1/2 (observed in `dash-upload.ndjson`)

Device initiates with type=0x81 channel open. Host then sends two sub-messages.

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

Note: TLV paths use UTF-16LE, but destination path in sub-message 2 uses **UTF-16BE**.

End-to-end file integrity uses **MD5** (transmitted alongside paths). On-device staging file named after MD5 hash.

**Session 4 sequence diagram:**

```
Device                                     Host
  │ ──── type=0x81 (channel open) ────────→  │  seq=0x0004
  │ ←─── fc:00 ACK ──────────────────────    │
  │ ←─── Sub-msg 1: path registration ───    │  7 chunks
  │ ──── fc:00 ACKs ─────────────────────→   │
  │ ──── Sub-msg 1 response (file ack) ───→  │  6 chunks
  │ ←─── Sub-msg 2: file content push ───    │  32 chunks
  │ ──── fc:00 ACKs ─────────────────────→   │
  │ ──── Sub-msg 2 response (file ack) ───→  │  6 chunks
  │ ←─── type=0x00 end marker ───────────    │
  │ ──── type=0x00 end marker ───────────→   │
```

**Sub-msg 1 / sub-msg 2 response format (device → host, ~318B, 6 chunks):**

Verified against `usb-capture/09-04-26/dash-upload.pcapng` (2026-04 firmware, CSP, 1355-byte mzdash upload). Both sub-msg responses share 8-byte-header + TLV-paths + trailing-metadata structure. Only `role` byte, `bytes_written` field, and trailing status byte differ.

| Offset | Size | Field | Value (sub-msg 1) | Value (sub-msg 2) |
|--------|------|-------|-------------------|-------------------|
| 0 | 1 | `role` | `0x01` | `0x11` |
| 1 | 1 | `max_chunk` | `0x38` (56) | `0x38` (56) |
| 2 | 1 | `ttype` | `0x01` (file transfer) | `0x01` |
| 3–7 | 5 | reserved | zeros | zeros |
| 8 | 1 | TLV marker | `0x84` (remote) | `0x84` |
| 9 | 1 | TLV separator | `0x00` | `0x00` |
| 10…R-2 | N | remote path | UTF-16LE NUL-term: `/home/root/_moza_filetransfer_md5_{md5hex}` | same |
| R | 1 | TLV marker | `0x8C` (local) | `0x8C` |
| R+1 | 1 | TLV separator | `0x00` | `0x00` |
| R+2…L-2 | M | local path | UTF-16LE NUL-term: host temp path | same |
| L | 1 | metadata flag | `0x10` | `0x10` |
| L+1 | 16 | MD5 | MD5 of received content | same |
| L+17 | 4 | `bytes_written` (BE u32) | `0x00000000` | `0x0000054B` (= total) |
| L+21 | 4 | `total_size` (BE u32) | `0x0000054B` (= uncompressed mzdash size) | same |
| L+25 | 4 | marker | `0xFFFFFFFF` | `0xFFFFFFFF` |
| L+29 | 1 | trailer / status | `0x6B` (in-progress) | `0x25` (complete) |

Interpretation:
- **Sub-msg 1 response** = "ack path registration; expecting `total_size` bytes, received 0." Host uses to confirm wheel ready, proceeds with sub-msg 2.
- **Sub-msg 2 response** = "ack file content; received all `total_size` bytes, MD5 matches." Host uses to confirm upload landed before sending type=0x00 end marker.
- `bytes_written` = `total_size` on sub-msg 2 = how wheel confirms whole file arrived.
- MD5 in metadata tail matches `{md5hex}` embedded in remote-path filename — content hash computed by wheel over decompressed mzdash after receipt.
- Trailer byte `0x6B` vs `0x25`: not fully decoded. Stable across repeated uploads of same file — probably status code (in-progress / complete) rather than CRC.

Both response structures chunked via standard `7c:00 type=0x01` SerialStream data chunks on session 0x04 with per-chunk CRC-32 trailers. Sim / reference implementation builds full ~318-byte message once, then pushes through `ChunkMessage(msg, session=0x04, seq)` for 6 wire chunks.

**2025-11 firmware note:** `automobilista2-wheel-connect-dash-change.pcapng` shows 2025-11 firmware's initial filesystem push on session 0x04 uses a DIFFERENT structure: subtype tag `0x0a`, 53-byte prefix (tag + LE size + BE path-length + UTF-16LE `/home/root` + `ff*8 00` padding + 14-byte unknown metadata) wrapping a zlib directory listing. Full byte-level layout documented in § Session 0x04 device → host root directory listing. That burst is NOT an upload response — it's a root directory listing. Under 2025-11, confirmation of fresh upload arrives as post-upload dir-listing refresh on session 0x04 (and updated configJson state blob on session 0x09), not as sub-msg 1/2 response with 2026-04 format. When implementing wheel sim: emit both paths based on `ttype`: `0x01` for per-sub-msg acks (2026-04 style), secondary root-listing refresh on END for 2025-11 parity.

### Session 0x04 device → host root directory listing (2025-11 firmware)

Shortly after session 0x04 opens, device pushes filesystem root listing so host can see what's on wheel before choosing to re-upload. Envelope **differs from session 0x09** — a 53-byte prefix precedes the zlib stream (verified 2026-04-22 by decoding `automobilista2-wheel-connect-dash-change.pcapng`):

```
Offset  Bytes                                           Meaning
0x00    0a                                              subtype tag
0x01    <size LE:4>     (e.g. d5 00 00 00 = 213)        byte count after this field
0x05    <pathlen BE:4><0x00>  (e.g. 00 00 00 14 00)     path length in bytes + null sep
0x0a    <UTF-16LE path>  (20 B for "/home/root")        utf-16 directory path
+path   ff ff ff ff ff ff ff ff 00                      9-byte padding sentinel
+9      de c3 90 00 00 00 00 00 00 00                   10-byte unknown metadata block
+10     a9 88 01 00                                     4-byte unknown (LE 100521 — not uncomp size)
-----   zlib deflate stream of the JSON listing
```

Decoded JSON body:

```json
{"children":[{"children":[],"createTime":-28800000,"fileSize":0,"md5":"d41d8cd98f00b204e9800998ecf8427e","modifyTime":1755251038000,"name":"temp"}],"createTime":-28800000,"fileSize":0,"md5":"","modifyTime":1755251038000,"name":"root"}
```

Children nest recursively. `createTime` of `-28800000` (–8 h in ms) is UTC epoch offset marker wheel firmware ships with. Semantics of the 14-byte unknown metadata block (`de c3 90 …` + `a9 88 01 00`) are not decoded — sim emits them verbatim and PitHouse still parses the listing, so they may be header padding the wheel firmware populates but the host ignores.

After each upload (and on initial connection), wheel pushes the listing on session 0x04 using this wrapper. Plugin reassembles via second `SessionDataReassembler` instance (`_session04Inbox`), decompresses JSON, logs child count. `_session04DirListingRefreshed` flips true on each complete listing.

**Earlier doc incorrectly claimed "same 9-byte envelope as configJson" for this message.** That shape decoded to garbage for the plugin-side parser; decompression succeeded only when the 53-byte prefix was stripped first. Sim now builds this envelope correctly (see `build_session04_dir_listing` in `sim/wheel_sim.py`, 2026-04-22).

### Dashboard config RPC (session 0x09, compressed transfer)

Chunk format is standard 9-byte compressed envelope (`flag + comp_sz + uncomp_sz + zlib`). Both directions use zlib-compressed JSON.

**Schema differs between firmware versions.**

**2026-04 firmware** (from `dash-upload.pcapng`):

Host → device `configJson()` canonical library list:
```json
{"configJson()":{"dashboards":["DNR endurance","Formula 1","GT V01","GT V02","GT V03","JDM Gauge Style 01","JDM Gauge Style 02","JDM Gauge Style 03","Lovely Dashboard for Vision GS","Rally V01","m Formula 1","rpm-only"],"dashboardRootDir":"","fontRootDir":"","fonts":[],"imageRootDir":"","sortTags":0},"id":11}
```

Device → host state (3 sequential blobs: `disabledManager` first, cleared mid state, then `enabledManager`):
```json
{"TitleId":4,"disabledManager":{"deletedDashboards":[],"updateDashboards":[{"createTime":"...","dirName":"rpm-only","hash":"...","id":"{uuid}","idealDeviceInfos":[{"deviceId":16,"hardwareVersion":"RS21-W08-HW SM-DU-V14","networkId":1,"productType":"Display"}],"lastModified":"...","previewImageFilePaths":[],"resouceImageFilePaths":[],"title":"rpm-only"}]},"enabledManager":{"deletedDashboards":[],"updateDashboards":[]},"imagePath":[{"md5":"...","modify":"...","url":"..."},...]}
```

**2025-11 firmware** (from `automobilista2-wheel-connect-dash-change.pcapng`) — renamed keys, different structure:

Host → device `configJson()` canonical library list:
```json
{"configJson()":{"dashboards":["Core","Grids","Mono","Nebula","Pulse","Rally V1","Rally V2","Rally V3","Rally V4","Rally V5","Rally V6"],"dashboardRootDir":"","fontRootDir":"","fonts":[],"imageRootDir":"","sortTags":0},"id":11}
```

Device → host state (single blob, no 3-sequence split):
```json
{"TitleId":1,"configJsonList":["Core","Grids",...,"Rally V6"],"disableManager":{"dashboards":[],"imageRefMap":{"MD5/abc.png":1,...},"rootPath":"/home/moza/resource/dashes"},"displayVersion":11,"enableManager":{"dashboards":[{"createTime":"","dirName":"Rally V1","hash":"...","id":"...","idealDeviceInfos":[{"deviceId":17,"hardwareVersion":"RS21-W08-HW SM-DU-V14","networkId":1,"productType":"W17 Display"}],"lastModified":"2025-11-21T07:45:36Z","previewImageFilePaths":["/home/moza/resource/dashes/Rally V1/Rally V1.mzdash_v2_10_3_05.png"],"resouceImageFilePaths":[],"title":"Rally V1"},...],"imageRefMap":{},"rootPath":"/home/moza/resource/dashes"}}
```

Key schema differences:

| Field | 2026-04 | 2025-11 |
|-------|---------|---------|
| Manager keys | `disabledManager` / `enabledManager` (with "d") | `disableManager` / `enableManager` (no "d") |
| Dashboard array | `updateDashboards` | `dashboards` |
| Also has | `deletedDashboards`, `imagePath` (top-level) | `imageRefMap` (nested), `rootPath`, `displayVersion`, `configJsonList` |
| `productType` | `"Display"` | `"W17 Display"` |
| `deviceId` | 16 | 17 |
| State blobs | 3 sequential (disable, empty, enable) | 1 blob |
| `TitleId` | 4 | 1 |

Both schemas list same per-dashboard metadata: `title`, `dirName`, `hash`, `id`, `idealDeviceInfos`, `lastModified`, `previewImageFilePaths`. Simulators must emit schema matching firmware host expects.

### Session 0x01 management RPC envelope

Management RPCs use `0xFF`-prefixed envelope:

```
FF(1)  inner_len(4 LE)  token(4 LE)  data(inner_len)  CRC32(4)
```

Token links requests to responses. Multi-chunk messages also have per-chunk CRC trailers. Message at t=5.2s in capture carries zlib-compressed device log (7163 bytes, UTF-16BE) listing installed dashboards and rendering status.

---

## Channel configuration burst (group 0x40, post-upload or on connect)

After dashboard file transfer (or on wheel connect without upload), Pit House sends burst of `0x40` commands configuring channel layout. Same burst used both contexts; CS V2.1 (no screen) receives same channel config as VGS (built-in screen). Channel indices and response values are dashboard-specific.

| Cmd | Data | Purpose |
|-----|------|---------|
| `09:00` | (none) | Begin/reset channel config |
| `1e:01` | `CC 00 00` | Enable channel CC on page 1 |
| `1e:00` | `CC 00 00` | Enable channel CC on page 0 — wheel responds `CC XXXX` (stored, e.g. `01f4`=500, `03e8`=1000, `0bb8`=3000) |
| `1c:00`/`1c:01` | `00` | Page configuration |
| `1d:00`/`1d:01` | `00` | Page configuration |
| `28:00` | `00` | Query active dashboard mode (wheel retains across power cycles) |
| `28:01` | `00` | Query active page number |
| `28:02` | `01 00` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |
| `1b:00`/`1b:01` | `FF value` | Brightness per page (value `64`=100%) |
| `1f:00`/`1f:01` | `FF idx 00 00 00` | LED color read per index (`idx`=`0a`–`0f` observed) |
| `27:00`–`27:03` | `00/01 00 00 00` | Page/dashboard config (sub-IDs 0–3, variants with `01`) |
| `29:00` | `00` | Display settings (TBD) |
| `2a:03` | `00` | Display settings (TBD) |
| various | — | Other display settings (`0a`, `0b`, `05`, `20`, `21`, `24`, etc.) |

Wheel `0x0e` debug log confirms channel config writes EEPROM: `"Table 2, Param 47 Written: 7614374"`.

**Cold-connect (no dashboard upload):** captured during CS → VGS swap in `cs-to-vgs-wheel.ndjson`. Pit House runs full identity probe then same channel configuration burst — no `7c:00` file transfer or `configJson()` RPC. Does not ask wheel which dashboard is active; pushes channel layout from internal state. `0xc0/13:00` response `00 ff ff` during setup may indicate "no active dashboard" or default state.

**Implication:** burst appears required on each wheel connection before telemetry frames accepted. Sending `7d:23` to fresh wheel without first sending `0x40` channel enables and `28:02 data=0100` may not work.

### 28:00/28:01/28:02 details

| Wire | Name (rs21_parameter.db) | Purpose |
|------|--------------------------|---------|
| `28:00 data=00` | `WheelGetCfg_GetMultiFunctionSwitch` | Query active dashboard mode. Wheel retains last loaded dashboard across disconnections. |
| `28:01 data=00` | `WheelGetCfg_GetMultiFunctionNum` | Query active page number |
| `28:02 data=01:00` | `WheelGetCfg_GetMultiFunctionLeft` | Set multi-channel telemetry mode (01=multi, 00=RPM only) |

Read-then-write pattern: Pithouse sends 28:00 and 28:01 (read state), then 28:02 (set mode) during burst. Wheel responds `00:00` to `28:02 data=01:00` — normal behavior, not failure.

**Normal operation:** `28 02 01 00` continues polling ~3.4 Hz to maintain multi-channel mode.

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

Bytes `46`, `80`, `fe 01` constant. No second short-form frame (unlike `7c:27`). Wheel→host direction (group 0xC3) uses `7c:23` with different byte layout to advertise channel catalog before session opens — see § Tier definition protocol.

**`7c:1e` display settings push** — sent by Pithouse to all wheel models (not VGS-specific). Brightness, timeout, orientation. Same structure as `7c:23`/`7c:27` with constant byte `6c`:

| Observed payload | Context |
|------------------|---------|
| `6c 80 0c 00 0a 00 fe 01` | With active dashboard pages (7c:27/7c:23 also cycling) |
| `6c 80 06 00 04 00 fe 01` | After dashboard switch / settings change (7c:27/7c:23 stop) |

b2/b4 values are sequence counters (same as 7c:27/7c:23), not display settings. Actual brightness/timeout values written via `grp 0x40` settings commands (`cmd 0x1b` = brightness, `cmd 0x1e` = timeout).

---

## LED color commands

RPM and button LED colors use `wheel-telemetry-rpm-colors` and `wheel-telemetry-button-colors`. Fixed payload size of 20 bytes per chunk; colors split across multiple writes.

Each LED: 4 bytes `[index, R, G, B]`. Five LEDs per chunk (5 × 4 = 20). With 10 RPM LEDs = 2 chunks. With 14 button LEDs = 3 chunks (last padded to 20 bytes).

**Padding:** use index `0xFF` for unused entries, not `0x00`. Zero-padding creates `[0x00, 0x00, 0x00, 0x00]` which firmware interprets as "set LED 0 to black", causing flicker.

---

## Other periodic commands

### Group 0x0E parameter table reader / debug console (host → devices 0x12/0x13/0x17, ~9 Hz)

Pithouse sends 158 per session. Host reads EEPROM parameters sequentially and receives firmware debug log output.

**Request format:** `7E 03 0E [device] 00 [table] [index] [checksum]`
- `table`: EEPROM table number (0x00 = base config, 0x01 = alt)
- `index`: parameter index, incremented sequentially (0x01, 0x03, 0x04, ...)

**Response format (group 0x8E):**
- **Parameter values** (cmd=00:00, n=7): `[index] 00 00 [value bytes]` — stored parameter at index
- **Debug log text** (cmd=05:xx, variable length): ASCII firmware log output, e.g.:
  - `"RFloss[avg:0.00000%] recvGap[avg:4.25699ms]"` — NRF radio stats
  - `"INFO]param_manage.c:340 Table 2, Param 43 Written: 0"` — EEPROM write confirmation

Debug log entries confirm `0x40/1E` channel config commands write to EEPROM. Diagnostic only — **not required for telemetry**.

Starts ~1s after session opens. Sent to base (0x12, 51 frames), wheel (0x17, 68 frames), pedals (0x13, 39 frames). Plugin does not implement.

Short-form host poll also sent ~1 Hz to device 0x13: 3-byte payload `00 01 XX` with 16-bit BE countdown counter starting at 0x013A (314). Base echoes back + 4 unknown bytes.

### Group 0x1F (host → device 0x12, ~3 Hz)

`4F XX 00/01` where XX cycles `08`→`09`→`0A`→`0B`. Response inserts `0xFF` status byte.

### Group 0x28 (host → device 0x13, occasional)

Queries device parameters from base unit. Request format: `[sub_id] 00 00`. Response mirrors sub_id with 2 data bytes.

Observed in `connect-wheel-start-game.json` (sent twice, ~2s apart):

| Sub-cmd | Response value | Notes |
|---------|---------------|-------|
| `0x01` | `01 C2` (450) | Base parameter |
| `0x17` | `01 C2` (450) | Wheel (device 0x17) parameter — possibly FFB strength/range |
| `0x02` | `03 E8` (1000) | Base parameter |

### Group 0x29 (host → device 0x13, once during config)

Sent once during dashboard config burst. Payload: `13 04 4C` (device 0x13, value 1100). Response mirrors exactly. Possibly timing/rate setting for base.

### Group 0x2B (host → device 0x13, occasional)

`02 00 00`, sent on state changes (pause, session end).

---

## Complete telemetry startup timeline

Two captures provide complementary views.

### Concurrent outbound streams during active telemetry

| Stream | Rate | Device | Group/Cmd | Purpose | Required? |
|--------|------|--------|-----------|---------|-----------|
| Sequence counter | ~45/s | base (0x13) | `0x2D/F5:31` | Frame sync to base | TBD |
| Telemetry enable | ~48/s | wheel (0x17) | `0x41/FD:DE` data=`00:00:00:00` | Mode/enable flag | Likely — entire session |
| **Live telemetry** | ~31/s | wheel (0x17) | `0x43/7D:23` | Bit-packed game data | Yes |
| Heartbeat | ~1/s each | all devices (18–30) | `0x00` n=0 | Keep-alive / presence | Likely |
| RPM LED position | ~4/s | wheel (0x17) | `0x3F/1A:00` | LED bar position | Separate feature |
| Telemetry mode | ~3/s | wheel (0x17) | `0x40/28:02` data=`01:00` | Set/poll multi-channel mode | Likely |
| Dash keepalive | ~1.5/s | dash (0x14), 0x15, wheel (0x17) | `0x43` n=1, data=`00` | Keep-alive for dash and wheel sub-devices | Yes — Pithouse sends to all three |
| Display config | ~1/s | wheel (0x17) | `0x43/7C:27` | Page-cycled display params | Yes |
| Dashboard activate | ~1/s | wheel (0x17) | `0x43/7C:23` | Declares active dashboard pages | Yes |
| Status push | ~1/s | wheel (0x17) | `0x43/FC:00` | Session ack with session=FlagByte and current ack seq (NOT zeros) | Yes — Pithouse uses real session/seq |
| Settings block | ~1/s | wheel (0x17) | `0x43/7C:00` | Config sync | No (file transfer) |
| Button LED | ~1/s | wheel (0x17) | `0x3F/1A:01` | Button LED state | Separate feature |

### Preamble detail — from `moza-startup.json` (2026-04-12, raw Wireshark JSON)

Most precise source, decoded directly from raw USB packets:

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

### Full connect-to-telemetry — from `connect-wheel-start-game.json`

Wheel plugged in cold, then Assetto Corsa started:

| Phase | Time | Events |
|-------|------|--------|
| **Idle** | t=0–7.8s | Heartbeats, keepalives, `0x0E` debug poll. Only dev18/19/23 respond |
| **Wheel detected** | t=7.82s | Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05 → 0x07 → 0x0F → 0x11 → 0x08 → 0x10 |
| **Config burst** | t=8.2–9.1s | ~50 `0x40` commands (channel enables, page config, LED config). `0x40/28:02` polling at ~3 Hz |
| **Dashboard upload** | t=21.4–23.5s | `0x43/7c:00` chunked file transfer. Display sub-device probed |
| **Pre-game** | t=24–30.5s | `0x40/28:02` polling (response always `00:00`), heartbeats, keepalives |
| **Game starts** | t=30.568s | `0x41/FD:DE` enable + `0x2D/F5:31` seq counter start simultaneously |
| **Telemetry** | t=30.600s | `0x43/7D:23` live data (flag=0x02). ~31 frames/s steady state |

---

## Plugin implementation

Replicates Pithouse's observed preamble with direct session allocation.

### Startup phases

**Phase 0 — Session open + config** (~200ms–1.2s, before timer starts):
1. Send type=0x00 end markers on ports 0x01..0x10 to reclaim stale sessions (e.g. from previous SimHub crash). Without this, stale session causes fresh open to be silently ignored. Sleep 100ms.
2. Send type=0x81 session opens for 0x01 (mgmt), 0x02 (telem = `FlagByte`), 0x03 (aux, fire-and-forget). Wait up to 500ms each for fc:00 ack. Proceed with PitHouse defaults if neither acks — real wheels silently accept data on these sessions even without explicit ack. `Start()` dispatched to background thread so serial read thread stays free to deliver fc:00 acks.
3. If `TelemetryUploadDashboard` enabled, upload `.mzdash` file on **session 0x04** (device-initiated file transfer, 2025-11 firmware) via `DashboardUploader.BuildUpload()` → `TierDefinitionBuilder.ChunkMessage()`. Plugin waits for device to open session 0x04 (type=0x81), ACKs, then sends sub-msg 1 (path registration) + sub-msg 2 (file content) per § Path B. Waits up to 2s for wheel acknowledgment, then send type=0x00 end marker. 500ms sleep after END so state-refresh burst arrives before upload phase returns.
4. Send sub-message 1 preamble (`07 04 00 00 00 02 00 00 00 03 00 00 00 00`) as 7c:00 data on telemetry session — prepares wheel's tier config parser.
5. Send tier definition as 7c:00 data chunks on telemetry session (channel indices, compression codes, bit widths). **Flag bytes 0x00-based, NOT session-port-based.**
6. Send Display sub-device identity probe via 0x43.

**Phase 1 — Preamble** (~1 second, timer running):
7. Ack incoming 7c:00 channel data on telemetry session with fc:00 (session=FlagByte).
8. Send heartbeats only — no telemetry, no enable, no channel config.
9. Detect Display sub-device from 0x87 model name response.

**Phase 2 — Active** (continuous, after preamble):
10. Send `0x40` channel config burst (1E enables for pages 0-1 channels 2-5, then 28:00, 28:01, 09:00, 28:02).
11. Begin `0x41/FD:DE` enable signal (~30+ Hz).
12. Begin `0x43/7D:23` bit-packed telemetry (flags 0x00/0x01/0x02, ~30 Hz per tier).
13. Begin `0x2D/F5:31` sequence counter (~30 Hz).
14. Begin periodic streams at ~1 Hz: heartbeats, dash keepalives (0x43 to dev 0x14, 0x15, 0x17), display config (7C:27) + dashboard activate (7C:23) interleaved per page, session ack (FC:00 with session=FlagByte and current ack seq).
15. Begin `0x40/28:02` telemetry mode polling (~3 Hz).

RPM LEDs (`0x3F/1A:00`) and button LEDs (`0x3F/1A:01`) handled separately by `MozaDashLedDeviceManager` and `MozaLedDeviceManager`. Zero preamble.

**Disable → re-enable:** `Stop()` resets `FramesSent`; caller clears dispatch guard so re-enable performs full fresh startup (new port probing, new tier definition, new preamble). Required because wheel's session state may have changed while telemetry disabled.

### Session management

Plugin opens sessions 0x01 (management), 0x02 (telemetry = `FlagByte`), 0x03 (aux config) directly with type=0x81 frames. 0x01 and 0x02 wait up to 500ms each for fc:00 ack. 0x03 opened fire-and-forget for doc compliance — plugin never writes on 0x03, but any unsolicited device data on 0x03 is ACKed to avoid wheel-retransmit stalls.

Device-initiated sessions (0x04/0x06/0x08/0x09/0x0a) accepted via `OnMessageDuringPreamble` handling type=0x81 frames: plugin echoes host's `open_seq` (payload bytes 6-7) in `fc:00` ACK with same session byte. Handler stays subscribed for entire active connection so session 0x04 post-upload directory refresh, session 0x09 configJson state updates, and session 0x0a RPC replies keep flowing beyond ~1s preamble window.

### Tier definition implementation

Plugin supports both versions, selectable via `TelemetryProtocolVersion` setting (UI: Telemetry > Advanced > Protocol version):

- **Version 2** (default): compact numeric tier definitions via `TierDefinitionBuilder.BuildTierDefinitionMessage()`. Flag byte assignment controlled by `FlagByteMode` (0=zero-based, 1=session-port, 2=two-batch).
- **Version 0**: URL subscription via `TierDefinitionBuilder.BuildV0UrlSubscription()`. Double-sent (once at startup, once after preamble) to match PitHouse. Flag byte mode not applicable — always zero-based.

Dashboard upload controlled by `TelemetryUploadDashboard` (UI: Telemetry > Advanced > Upload dashboard, default: on). Uploads `.mzdash` file to wheel on **session 0x04** (2025-11 firmware file-transfer path, Path B below) using TLV-path + MD5 sub-msg 1/2 framing. Path A (session 0x01 FF-prefix) was the original pre-2025-11 implementation; replaced because 2025-11 firmware only actions mzdash writes via session 0x04. Mzdash content loaded from user-selected file or from embedded resource matching active profile name.

Plugin parses wheel's incoming channel catalog (session 0x02 tag 0x04 URLs) during preamble and displays detected channels in UI. Catalog also used to **filter tier definition** (`FilterProfileToCatalog`) before sending — channels in profile whose URL doesn't appear in wheel's advertised set are dropped, along with any tier ending up empty. Match case-insensitive on full URL, with last-path-segment fallback. Falls back to unfiltered profile if filtering removes everything.

Before transmitting tier definition, plugin calls `WaitForChannelCatalogQuiet(quietMs=200, timeoutMs=2000)` so wheel's pre-tier-def channel-registration burst (session 0x02 tag 0x04 entries) finishes arriving first. Without this wait, fast connections can race tier def against wheel's own catalog push and wheel rejects tier def.

### Reassembly fallback

`SessionDataReassembler.TryDecompress` first tries offset-based 9-byte envelope (correct for sessions 0x09 and 0x0a). If that fails (because session 0x04 uses a 53-byte prefix, session 0x03 a 12-byte wrapper, or because embedded 0x7E bytes in mzdash JSON payload shifted an otherwise-valid header), falls back to `TryDecompressByMagic` which scans for `78 9c` / `78 da` zlib magic bytes and trial-decompresses each hit. Mirrors `sim/wheel_sim.py`'s `_scan`. The magic-scan fallback is what kept the plugin parsing session 0x04 dir listings correctly even before sim's envelope was matched to the real-wheel format (2026-04-22).

---

## Setting value encoding

Several configuration commands use non-obvious value encoding. Confirmed by cross-referencing Pithouse USB captures with boxflat source.

### Wheel settings (group 0x3F/0x40, device 0x17)

| Command | ID | Raw values | Notes |
|---------|-----|-----------|-------|
| paddles-mode | `03` | 1=Buttons, 2=Combined, 3=Split | **1-based**. Sending 0 is invalid — causes firmware to break all paddle input including shift paddles |
| stick-mode | `05` | 0=Buttons, 256=D-Pad | 2-byte field; D-Pad sets high byte (`0x0100`) |
| rpm-indicator-mode | `04` | 1=RPM, 2=Off, 3=On | **1-based** (wheel only) |

### Dashboard settings (group 0x32/0x33, device 0x14)

| Command | ID | Raw values | Notes |
|---------|-----|-----------|-------|
| rpm-indicator-mode | `11 00` | 0=Off, 1=RPM, 2=On | **0-based** — different from wheel |
| flags-indicator-mode | `11 02` | 0=Off, 1=Flags, 2=On | **0-based** |

Wheel and dashboard use different base indices (wheel 1-based, dashboard 0-based).

See [serial.md](serial.md) and [serial.yml](serial.yml) for full command tables.

---

## EEPROM direct access (group 0x0A / 10)

Low-level EEPROM read/write, applicable to any device. Bypasses named command interface. Found in rs21_parameter.db but not observed in USB captures. See [serial.md § EEPROM direct access](serial.md#eeprom-direct-access-group-0x0a--10--any-device).

EEPROM tables: 2=Base (38 params), 3=Motor (76 params, PID/encoder/field-weakening), 4=Wheel (123 params), 5=Pedals (45 params), 11=Unknown (8 params).

---

## Base ambient LED control (groups 0x20/0x22 — 32/34)

Controls 2 LED strips (9 LEDs each) on wheelbase body. Group 32 = write, group 34 = read. Sent to main device (0x12). Found in rs21_parameter.db but not observed in USB captures. See [serial.md § base ambient LEDs](serial.md#group-0x20--0x22-32--34--base-ambient-leds).

---

## Wheel LED group architecture (groups 0x3F/0x40 — 63/64, extended)

rs21_parameter.db reveals newer wheels organize LEDs into **5 independently controlled groups**. See [serial.md § extended LED group architecture](serial.md#extended-led-group-architecture-groups-0x3f--0x40).

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |

---

## Internal bus topology (monitor.json)

`monitor.json` file in Pit House installation defines device tree for each base model. These are **internal bus IDs**, not serial protocol device IDs. Mapping: bus 2 → main (0x12), bus 3 → base (0x13), bus 4 → wheel (0x17), bus 5 → dash (0x14), etc.

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

## SimHub plugin vs PitHouse wire divergence (2026-04-21)

Side-by-side capture with both clients connected to independent wheel_sim instances (SimHub on `/dev/tnt0`, PitHouse on `/dev/ttyGS0`, VGS model). Frames observed by sim = frames **sent by client**.

### Functional split (expected divergence)

PitHouse is dashboard/config manager. SimHub plugin is telemetry feeder. Each owns distinct protocol paths; neither does full set.

| Behaviour | PitHouse (42s window) | SimHub (746s window) | Notes |
|-----------|----------------------|----------------------|-------|
| Telemetry frames pushed (0x43/7D23) | 0 | 18501 | SimHub-only job |
| `0x41/FDDE` dash-telemetry-enable (~100 Hz) | 0 | 17473 | SimHub-only |
| `0x2D/F531` base sequence counter (~50 Hz) | 0 | 17335 | SimHub-only |
| Uploaded zlib blobs (tile_server maps + UTF-16 display strings) | 5 (sessions 0x02, 0x03) | 0 | PitHouse-only — config push |
| Catalog frames back from wheel (`catalog_sent`) | true | false | PitHouse triggers wheel to return channel catalog; SimHub never requests |
| Identity handshake frames | 7 | 0 | PitHouse probes wheel identity every connect; SimHub skips |
| `fw_debug` subscription (group 0x0E dev 0x17) | 522 frames (incrementing seq) | 0 | Diagnostic-only. SimHub correctly skips |

### Probe frame differences

| Probe | PitHouse | SimHub plugin | Documented? |
|-------|----------|---------------|-------------|
| Base probe (group 0x2B dev 0x13) | `7E 03 2B 13 02 00 00 CE` | `7E 03 2B 13 01 00 01 CE` (pre-fix) → now `02 00 00 CE` | FIXED 2026-04-21: `BaseProbeFrame` in `MozaSerialConnection.cs:469` now matches PitHouse pattern |
| Hub probe (group 0x64 dev 0x12) | not sent | `7E 03 64 12 03 00 00 07` | `(0x64, 0x12, 03)` documented in `sim/wheel_sim.py` as "hub-port1-power probe". PitHouse does not use — SimHub plugin-specific |

### Periodic polling not done by PitHouse

SimHub plugin sends these ~0.36 Hz (panel-timer-gated, only fires while settings panels visible):

| Frame | Sim label |
|-------|-----------|
| `7E .. 40 15 1C 00 ..` | wheel settings read cmd=1c 00 dev=0x15 |
| `7E .. 40 15 18 00 ..` | wheel settings read cmd=18 00 dev=0x15 |
| `7E .. 40 13 1C 00 ..` | wheel settings read cmd=1c 00 dev=0x13 |
| `7E .. 40 13 18 00 ..` | wheel settings read cmd=18 00 dev=0x13 |
| `7E .. 5B 1B 01 00 ..` | handbrake-direction probe |
| `7E .. 23 19 01 00 ..` | pedals-throttle-dir probe |

Wheel sim tags as unhandled. Plugin-side settings-panel polls (in `Devices/` code paths). No PitHouse equivalents during normal session.

### One-shot writes unique to SimHub

Seen once each in 746 s, tagged unhandled by sim — plugin issued, no PitHouse counterpart:

- `0x1F 0x12 cmd 33 00` / `0x1F 0x12 cmd 08 00` — main hub settings
- `0x3F 0x17 cmd 04 01` / `0x3F 0x17 cmd 07 00` / `0x3F 0x17 cmd 14 00` — wheel RPM/LED telemetry reads
- `0x33 0x14 cmd 0b/0a/07/11` variants + `0x32 0x14 cmd 0a/0b/11` variants — dash settings reads/writes

Most trace to per-device settings controls under `Devices/` exercised when wheel is first detected.

### Session counts

| Metric | PitHouse (142 s) | SimHub (746 s) | Rate ratio |
|--------|-----------------|----------------|-----------|
| `session_open` count | 3 | 7 | SimHub opens more sessions over time |
| `session_end` count | 9 | 33 | Both reset sessions repeatedly; SimHub higher |
| `proactive_sent` (sim→client) | 415 (~2.9/s) | 812 (~1.1/s) | Sim fires more proactive opens at PitHouse — suggests SimHub not fully acking or not driving state that causes wheel to open more |
| `session_ack_in` | 41 | 797 | SimHub acks much more traffic (reflects telemetry volume) |

### What SimHub is missing vs PitHouse

If dashboard-management parity with PitHouse is ever a goal:

1. **Identity handshake** — SimHub sends Display sub-device probe via 0x43 (`SendDisplayProbe`) but previously did not send the 7 top-level PitHouse-style identity probes (direct groups 0x09/0x02/0x04/0x05/0x06/0x08sub2/0x11 to dev 0x17). PitHouse fires all 12 frames (7 direct + 5 via wheel-model-name/sw-version/hw-version/serial-a/serial-b) on every connect. **FIXED 2026-04-21**: `MozaDeviceManager.SendPithouseIdentityProbe(deviceId)` added. Fires 7 PitHouse direct-group identity frames not covered by existing `ReadSetting` calls: `0x09` (presence/ready), `0x02` (device presence), `0x04` (device type), `0x05` (capabilities), `0x06` (hardware ID), `0x08 cmd=02` (HW sub-version), `0x11 cmd=04` (identity-11). Called at wheel-detection point in `MozaPlugin.cs` for both new-protocol and old-protocol branches. Brings SimHub to 12-frame PitHouse identity parity on connect.
2. **Wheel-returned channel catalog** — SimHub never triggers wheel to send channel catalog back. `catalog_sent=false` in sim status. PitHouse completes this exchange within seconds.
3. **Config blob upload on session 0x02/0x03** — PitHouse uploads UTF-16 display-string tables and tile-server map JSON during startup. SimHub uploads none. See Phase 3 below.
4. **fw_debug / EEPROM parameter reader** — diagnostic only, can stay unimplemented.

### Items likely worth fixing in SimHub

- **Periodic settings polls** (not a bug): 0.36 Hz rate observed was panel-timer-gated — `MozaWheelSettingsControl` uses `DispatcherTimer(500ms)` started on `Loaded`, stopped on `Unloaded`. Similar gating in `MozaDashSettingsControl`. No fix needed; polls only flow while panels visible.
- **One-shot dash-settings writes on connect** (not a bug): legitimate UI initialisation triggered by `MozaDashSettingsControl.RefreshDash()` on first panel open. Reads current state, writes defaults if unset.

### Phase 3 — dashboard/config upload blobs (NOT implemented in SimHub)

PitHouse uploads 5 zlib-compressed blobs during connect that SimHub does not send. These populate wheel's native dashboard UI (channel-name lookup tables + map tiles). SimHub is telemetry feeder, not dashboard manager, so telemetry works without them. Implementation would be ~multi-day RE with limited benefit unless SimHub takes over "native wheel UI" role.

**Session 0x02, blob 1 (~7.2 KB)** — channel-name dictionary
- UTF-16LE strings, length-prefixed, tagged entries
- Content observed: `RpmAbsolute1..10`, `RpmPercent1..N`, similar telemetry-channel display names alphabetical
- Each entry appears `[tag_u16_le] [string_id_u16_le] [utf16_len_u16_le] [utf16le_bytes] [null_u16]` — exact layout not fully reversed
- Preamble: 59-byte offset (matches PitHouse FF-prefixed upload framing)
- Purpose: lets wheel's dashboard UI render channel names without embedding them in firmware; supports localisation and channel additions without FW updates

**Session 0x02, blob 2 (~9.9 KB)** — input-action-name dictionary
- Same UTF-16LE tagged layout
- Content observed: action/command names — `decrementEqualizerGain1..6`, `decrementGameForceFeedbackFilter`, similar wheel-action identifiers
- Purpose: label pickable actions in wheel's button-binding UI

**Session 0x03, blob 1 (~775 B)** — empty tile-server map metadata
- zlib-compressed JSON: `{"map":{"ats":"<inner_json_string>","ets2":"<inner_json_string>"},"root":"...","version":...}`
- Inner `ats`/`ets2` values JSON-escaped strings with fields `bg, ext_files, file_type, layers, levels, map_version, name, pm_support, pmtiles_exists, root, support_games, tile_size, version, x_max/min, y_max/min`
- Empty/default state: all zero / empty arrays. PitHouse sends when no map tiles installed
- `root` field is host-side tile-server path (e.g. `C:/Users/giant/AppData/Local/Temp/tile_server/ats`)

**Session 0x03, blobs 2+3 (~3 KB and ~6.3 KB)** — populated map metadata
- Same JSON schema as blob 1
- `layers` array populated with per-zoom-level tile counts, `ext_files: ["cities.json","file_map.json"]`, `file_type: "png"`, `bg: "#ff303030"`
- Sent once tile data exists on host for respective game (ATS or ETS2)
- Purpose: wheel's integrated map display for American Truck / Euro Truck Simulator 2; driven from PitHouse's local tile_server directory

**Decision (2026-04-21)**: Deferred. Needs:
1. Full tagged-UTF-16 binary layout reversed (bit-level) from multiple captures
2. Complete channel-name + action-name enumeration (can source from `rs21_parameter.db` for action names; channel names from `Telemetry.json`)
3. Tile-server JSON schema lock-in (mostly visible already, but `version` field semantics unclear)
4. Wiring into existing `Dashboard upload protocol` framing (session 0x02 / 0x03 chunked writes with CRC-32 per chunk)

Punt until concrete use case for SimHub driving wheel's native dashboard UI. `.mzdash` upload path (already implemented on session 0x01) covers dashboard-body case; these blobs purely cosmetic UI metadata on top.

### Phase 4 — fw_debug subscription (NOT implemented, intentional)

PitHouse subscribes to `group 0x0E dev 0x17` and receives ~522 incrementing-seq debug frames per session. Content is ASCII firmware log output. Diagnostic-only — not required for telemetry. Skipping is correct for SimHub.

### Session 0x09 configJson burst timing (real wheel vs sim)

Measured on `usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng` (VGS + 2025-11 firmware). Real wheel's device→host session 0x09 state push on connect:

| Metric | Real wheel | wheel_sim (chunk_size=54 fix) |
|--------|------------|-------------------------------|
| Total chunks | 32 (seq 0x000A–0x0029) | 8 (seq 0x0100–0x0107) |
| Wire N per chunk | 64 | 64 |
| Net data per chunk | 54 B + 4 B CRC | 54 B + 4 B CRC |
| Total window | ~90 ms | ~40 ms |
| Avg inter-chunk gap | ~3 ms | ~6 ms |
| Min inter-chunk gap | 0.0 ms (same USB microframe) | ~6 ms |

**Real hardware bursts FASTER than the sim**, so any plugin that keeps up on real HW will also keep up with sim. In production (native Windows + USB URB bulk transfers), the plugin receives consolidated byte blocks from kernel-side URB completions and parses cleanly.

#### Chunk body size — wire N field must stay ≤ 64

Real-wheel session 0x09 chunks carry N=64 on wire: 6-byte `7C:00:sess:01:seqL:seqH` header + 54-byte net payload + 4-byte per-chunk CRC32 trailer = 58-byte frame body + group/device = N=64. Confirmed by decoding `automobilista2-wheel-connect-dash-change.pcapng` chunks: strip trailing 4 bytes per chunk + concat → valid zlib stream (comp=1709, uncomp=7231).

Frame length field N is a single byte (0–255), but the plugin's framer enforces `payloadLength > 64 → reject` (`Protocol/MozaSerialConnection.cs:237`) to match the observed real-wheel upper bound. Any chunker that emits N>64 trips the reject path: the 0x7E at the start is consumed, cursor advances by one byte, and the framer resyncs byte-by-byte through the chunk body. When it hits a stray 0x7E inside the compressed payload it will attempt a bogus parse, log a single `DROP checksum mismatch` with nonsense group/device, and keep resyncing until it reaches the next valid frame start — which is usually the short final chunk of the same burst (often just under N=64 by accident).

Earlier `chunk_session_payload(chunk_size=58)` default in `sim/wheel_sim.py` produced N=68 (58 net + 4 CRC + 6 header = 68). Under that setting session 0x09 bursts reliably lost 6 of 7 chunks on both Linux (tty0tty) and Windows (USB CDC gadget → SimHub / PitHouse). Identical DROP byte pattern `3E-62-A5-A3-E1-79-03-99` appeared on both systems, proving the bytes arrived but the framer rejected each N=68 frame silently. Fix (2026-04-22): default `chunk_size=54` so chunk body + CRC + header totals N=64, matching real wheel. After the fix, all 8 session-0x09 chunks and all 4 session-0x04 dir-listing chunks parse cleanly in the SimHub plugin, and PitHouse Windows ingests the configJson state without issue.

---

## PitHouse-observed deviations (2026-04-21 sim captures)

Recorded during live PitHouse ↔ wheel_sim testing. Each item documents a behaviour seen on wire that deviates from, refines, or extends claims elsewhere in this doc.

### configJsonList is NOT factory-canonical

Doc § 857 shows an 11-name list (`Core, Grids, Mono, Nebula, Pulse, Rally V1..V6`) extracted from `automobilista2-wheel-connect-dash-change.pcapng`. These are NOT factory-canonical dashboards shipped by MOZA — they are the user's already-installed dashboards for that wheel. Different captures from different wheels will show different lists.

**Practical consequence:** `configJsonList` derives from current wheel-installed dashboard directory names, not from a fixed firmware catalog. A factory-fresh wheel's `configJsonList` is likely empty or shorter than the observed 11. Sim implementations should derive the list from `enableManager.dashboards[].dirName` rather than hardcoding names — `build_configjson_state()` in `sim/wheel_sim.py` does exactly this (2026-04-22 change).

**Empty-list behaviour confirmed (2026-04-22):** re-tested on Windows PitHouse + real USB CDC gadget. Emitting `configJsonList=[]` with every other top-level field populated kept PitHouse at `sessions_opened=0`, `tier_def_received=false` indefinitely. Restoring a non-empty list (the factory 11-name placeholder as a safety fallback for empty FS) brought the handshake back within a single reconnect. **Rule: keep at least one entry in `configJsonList` at all times.** Sim currently falls back to the factory 11-name list when FS is empty; once the exact gating condition is reverse-engineered from firmware, the fallback can be tightened (a single placeholder name may suffice).

### Session 0x0a RPC id is target-scoped, not counter

Doc § 663 previously implied sequential id assignment. Captures of 4 consecutive "Reset Dashboard" clicks in one PitHouse session all carried identical `id=13`; a separate earlier session used `id=15` for a different click. Id is a session-scoped target reference assigned once by PitHouse per item, reused across every RPC call targeting that item.

### `completelyRemove` arg does not match sim-advertised ids (2026-04-22)

Testing with Windows PitHouse against the USB-CDC gadget sim confirmed that the `<uuid>` PitHouse sends in `completelyRemove()` is **never** the id the sim advertised in its most recent `enableManager.dashboards[].id` push. Observed uuids across five delete clicks:

- `gLib1v4iWa5XZBCDew8R71yImlYyyaBC` — 32-char random string (factory-id format)
- `{b6fd8a33-8b10-4c32-8451-7e97c6073f83}` — random Microsoft GUID
- `{00000000-0000-0000-0000-000000000002}`, `{…000000000003}`, `{…000000000004}` — all-zero placeholders with varying last byte
- `{177b97ff-43f4-4fa3-bc27-9db20449c165}` — another random GUID
- `{2e869528-6e4e-4d08-a4cb-0c3981c42df0}` — another random GUID

Sim's synthetic id format (`sim-<md5[:8]>-<dirName>`) never appeared. PitHouse draws the uuid from its own per-install cache rather than echoing whatever `enableManager.dashboards[].id` the wheel most recently reported. This matches the "PitHouse local cache" hypothesis from § PitHouse cache-skip prevents upload.

**Sim-side practical handling** (implemented in `_handle_rpc` / `completelyRemove` branch of `sim/wheel_sim.py`):

1. Try exact match on every FS-derived `id`, `dirName`, `hash`, `title`.
2. Try a `_pithouse_dashboard_ids[arg]` lookup populated opportunistically from any `configJson()` host→wheel reply the sim observes.
3. Try matching against the captured factory `enableManager.dashboards[].id` list (for factory-preset dashboards).
4. **Last-resort fallback:** if FS contains exactly one non-factory dashboard, delete it anyway — PitHouse's UI is the ground truth for "user wants this deleted" and the sim has no other way to resolve the uuid.
5. **Always fire `_fire_state_refresh()` after handling** (even on no-op) so PitHouse's Dashboard Manager re-syncs against the current wheel state. Without the refresh, PitHouse caches a stale view and will re-issue the same delete on the next UI interaction.

Reply on session 0x0a uses the mirrored-key shape `{"completelyRemove()": "", "id": <same N>}` with the 9-byte envelope (§ Compressed transfer format). Sim's earlier `{"id": N, "result": {...}}` shape was silently dropped by PitHouse.

### PitHouse cache-skip prevents upload RPC under some condition we can't bypass from sim

Observed: clicking "Upload Dashboard" on a brand-new dashboard "horse" / "lol" / "test" produced ZERO host→wheel traffic on session 0x04 across multiple experiments, even with:
- Fresh sim filesystem (0 files, `enableManager.dashboards=[]`)
- Randomised `hw_id` / `serial0` per sim start (see `_apply_model` in `sim/mcp_server.py`)
- Randomised Display sub-device `hw_id` / `serial0` per sim start
- Empty `/home/moza/resource/dashes/` in session 0x04 root-dir listing
- Full 11-field configJson state schema matching real capture

PitHouse appears to cache "dashboard X already uploaded to wheel Y" entirely PC-side — keyed by something we could not identify (possibly a hash of wheel serials that doesn't match our randomised versions, or a persistent local DB at `%APPDATA%\MOZA Pit House\`). The `mcUid` mechanism documented at § 702 (`MainMcuUidCommand`) is the most likely cache key but its wire format remains unreversed (see `usb-capture/main-mcu-uid-re.md`).

Workaround to force upload: wipe PitHouse local data (`drive_c/users/steamuser/AppData/Local/MOZA Pit House/` + `Documents/MOZA Pit House/`) while PitHouse is closed.

### Display sub-device identity randomisation required, not just wheel identity

Plugin-side `_apply_model` in `sim/mcp_server.py` randomises both `model['hw_id']` AND `model['display']['hw_id']` on every sim start because PitHouse probes the display sub-device independently via 0x43 → dev 0x17. Random wheel identity alone is insufficient — dashboard management operations key on display identity, not base wheel identity.

### Session 0x04 root dir listing: persistent paths expected

Doc § 823 shows listing with `{"name":"temp"}` under root. In practice, a factory-fresh wheel keeps `/home/moza/resource/dashes/` as a persistent directory path even with no dashboards installed. PitHouse needs this path present in the listing to know where uploads should land. Sim `_synthesize_empty_fs_skeleton()` returns the 5-level `root/home/moza/resource/dashes` tree when FS is empty.

### Pithouse does not re-push dictionary blobs on reconnect

Doc § 1220 claims PitHouse uploads 5 zlib blobs (session 0x02 channel-name + action-name dictionaries, session 0x03 tile-server) on every connect. Observed: on sim_reload without Pithouse cycling, blobs did NOT re-transmit — Pithouse dedupes by its own state tracking. Only a fresh PitHouse connection (after wheel re-enumerate or PitHouse restart) triggers the full 3-blob push.

### Canonical RPC method envelope variants

Doc § 663 says session 0x0a uses the same 9-byte `[flag=0x00][comp_size+4 LE][uncomp_size LE]` envelope as session 0x09. Verified. But **session 0x04 root-directory listing** uses a 53-byte prefix (tag `0x0a` + size + UTF-16LE path + padding + metadata; see § Session 0x04 device → host root directory listing) and **session 0x03 tile-server** uses a **12-byte** envelope with `FF 01 00 ... FF 00 ...` + u24 BE uncompressed size — documented in § Session 0x03 tile-server envelope above. Same zlib body, different wrapper on every session. Do not assume one envelope shape fits all zlib-carrying sessions.

### Session 0x03 is host→wheel ONLY (verified 2026-04-22)

Scanned 5 captures for device→host traffic on session 0x03:

| Capture | Host→device | Device→host |
|---------|------------|-------------|
| `automobilista2-wheel-connect-dash-change.pcapng` | 82 | **0** |
| `automobilista2-dash-change.pcapng` | 17 | **0** |
| `connect-wheel-start-game.pcapng` | 90 | **0** |
| `12-04-26/moza-startup.pcapng` | 80 | **0** |
| `09-04-26/dash-upload.pcapng` | 1 | **0** |

**Wheel never pushes on session 0x03.** Session is one-way for PitHouse → wheel tile-server state uploads. Plugin's session 0x03 inbound parser (`TileServerStateParser`) stays dormant in real-wheel operation — kept for future firmware behaviour changes but no capture-driven requirement to exercise.

### Dashboard upload traffic missing when PitHouse thinks wheel has dashboard

Even with `enableManager.dashboards=[]` and filesystem listing showing empty `/home/moza/resource/dashes/`, PitHouse's UI sometimes displays dashboards as "already on device" and suppresses the upload RPC entirely. Indicates PitHouse keeps its own "what I last pushed to this wheel" record separate from what the wheel's session 0x09 state reports. Re-sync to empty requires either clearing PitHouse local cache OR presenting an entirely new wheel identity.

### configJson state push includes top-level fields many docs omit

Doc § 857 captures the full 11-key schema (`TitleId, configJsonList, disableManager, displayVersion, enableManager, fontRefMap, imagePath, imageRefMap, resetVersion, rootDirPath, sortTag`). Earlier sim builds only emitted 5 (TitleId, configJsonList, disableManager, displayVersion, enableManager) and PitHouse rejected the state / failed to progress tier def. All 11 fields must be present; factory-fresh values for the missing 6 are `fontRefMap={}, imagePath=[], imageRefMap={}, resetVersion=10, rootDirPath="/home/moza/resource", sortTag=0`.

## Open questions

- **Dashboard upload path ambiguity** — Two upload structures documented in § Dashboard upload protocol (Path A session 0x01 FF-prefix vs Path B session 0x04 sub-msg 1/2). Not yet confirmed whether these are:
  - (a) Two different upload paths firmware supports in parallel
  - (b) Same path described from different capture/understanding eras (one is stale)
  - (c) Different firmware versions (2026-04 vs 2025-11)

  Plugin currently implements Path B. Needs side-by-side capture on both firmware versions to resolve whether Path A is still needed on older firmware.

- **Dashboard byte limit configuration** — stored at config object offset `+0x30`, set during dashboard upload (group 0x40). Exact mechanism for setting this limit not yet traced.

- **Cold-start initialization** — EEPROM persistence across power cycles confirmed for channel config; unclear for session state.

- **MDD (standalone dash)** — no captures of telemetry sent to device 0x14; protocol may differ.

- **Dashboard upload: per-field pacing** — Plugin sends all upload chunks (across all 3 FF-prefixed fields) in a single burst, then waits for ack. PitHouse may instead pace by field: send field 0 chunks → wait for ack → send field 1 → wait → send field 2. Burst approach matches how tier definitions are sent (also tight-loop, working). If large dashboards fail while small ones succeed, try adding per-field ack waits.

- **Dashboard upload: seq=2 assumes port=1** — Data chunks on mgmt session start at seq=2, assuming session open used seq=1 (i.e. mgmtPort=1). Session open frame uses seq=port, so data should start at port+1. Since serial port is exclusive (PitHouse cannot run simultaneously), port probing always finds ports 1 and 2, making seq=2 correct in practice. Same assumption in tier definition code (seq=3, assumes telemetry port=2). If this changes (e.g. multi-client over network), both need to use `port + 1` instead of hardcoded values.

- **EEPROM direct access** — group 10 protocol found in rs21_parameter.db but never observed in USB captures; needs live verification.

- **Base ambient LEDs** — groups 32/34 commands found in rs21_parameter.db; not captured in USB traces (requires base with LED strips).

- **Wheel LED groups 2-4** — Single (28), Rotary (56), Ambient (12) groups found in rs21_parameter.db; only groups 0 (Shift/RPM) and 1 (Button) confirmed in captures so far. **Partial plugin support (2026-04-19)**: commands added for `1F [G] FF [N]` per-LED color, `1B [G] FF` brightness, `1C [G]` mode; experimental Wheel Settings panel exposes per-slot Range (min/max) + Fill/Clear/Send-one/Brightness/Mode controls for groups 0-4 plus Meter flag LEDs (slot 5). Brightness-read probe lights groups 2/3/4 panels when firmware answers — **probe unreliable** (firmware acknowledges reads for parameters with no physical hardware; confirmed on base KS wheel which has no rotary/ambient hardware but responds to all three probes). Use panel's summary TextBox to record per-wheel support and feed back into `WheelModelInfo`. No live telemetry equivalent (`25 G` / `26 G` bulk+bitmask) found for groups 2-4 — diagnostic uses static per-LED writes only.

- **Group 0x09 semantics** — presence/ready check sent first during probe. Response `00 01` may indicate sub-device count (VGS has 1 Display sub-device). Needs verification with other wheel models.

- **Group 0x28 / 0x29 purpose** — group 0x28 queries base for per-device parameters (values 450, 1000 seen); group 0x29 sets base parameter (value 1100). Possibly FFB or calibration related.
