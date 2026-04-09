# Moza Racing serial connection protocol

### Table
<table>
<thead>
<tr>
<th colspan=4>Header</th>
<th colspan=2>Payload</th>
<th rowspan=2>Checksum</th>
</tr>
<tr>
<th>Start</th>
<th>Payload length</th>
<th>Request group</th>
<th>Device id</th>
<th>Command id</th>
<th>Value(s)</th>
</tr>
</thead>
<tbody>
<tr>
<td>0x7e</td>
<td>1 byte</td>
<td>1 byte</td>
<td>1 byte</td>
<td>1+ byte</td>
<td>n bytes</td>
<td>1 byte</td>
</tr>
</tbody>
</table>

If a command id is an array of integers, you must provide them sequentially in the same order

Values are transmitted in big-endian.

### Checksum calculation
Checksum is the reminder of sum of the bytes divided by 0x100 (mod 256)
ChecksumByte8mod256

This sum includes the USB device serial endpoint (always 0x02), type (URB_BULK -> 0x03) and probably
the whole message lenght (typically 0x08), although this could be a bug in Moza Firmware, as even with longer messages, changing this last part of the "magic value" causes devices to not respond.

**Magic value = 13 (0x0d)**

### Responses

**Request group** in response has `0x80` added, so when reading request group `0x21` we should expect a response group of `0xa1`. The MSB indicates response direction.

**Device id** has its byte halves swapped. When reading/writing to device `0x13 (base)`, response will contain device `0x31` and so on.

**Payload length** in the response reflects the data the device sends back, not the request payload length. For write requests the response payload mirrors the request. For read requests the response payload contains the current stored value regardless of how many bytes the request sent — in practice this means a minimal read probe (e.g. payload = just the command ID, 1 byte) will receive a full-length response (e.g. 16 bytes of string data).

Checksum calculation is the same as for requests.

### Devices and commands
The list of device ids and command data can be found in the [serial.yml](serial.yml) file.

### Command chaining
You can send multiple commands at once. The device sends back all responses, but **not necessarily in the same order as the requests**. Responses are matched to requests by group number, not by position in the stream.

### Unsolicited messages
Some devices emit packets without a corresponding request. Observed examples:

- **Group 0x0E** from the wheel (device 23): firmware debug/log text as ASCII, pushed ~every 2 s. Contains NRF radio stats and status strings, e.g. `NRFloss[avg:0.00000%] recvGap[avg:4.70100ms]` and `Wheel Uart is connected`. Filtered in practice.
- **Group 0x06** from the wheel (device 23): emitted spontaneously on connection, ~12 bytes, contains what appears to be a partial hardware identifier. Purpose unknown.

---

## Pithouse USB telemetry (dash.json capture analysis)

> **Capture context:** Assetto Corsa, started paused → unpaused and drove briefly → paused → stopped.
> Device: Moza composite USB device (VID 0x346E PID 0x0006). Two interfaces: MI_00 = USB serial (COM8, the Moza protocol bus), MI_02 = HID (wheel axes/buttons). All serial protocol frames are transmitted over USB bulk endpoint 0x02 (OUT) / 0x82 (IN). The HID interrupt endpoints (0x03/0x83) carry button/axis input — not telemetry.

> **Physical topology:** Device IDs (19=base, 20=dash, 23=wheel, etc.) are addresses on the internal serial bus — not separate USB devices. All messages go over the single COM8 serial interface and the wheelbase hub routes each to the right physical peripheral. **Notably, all live telemetry is addressed to device 23 (wheel), not device 20 (dash).** The dash display is driven by the wheel firmware internally — pithouse does not send telemetry directly to device 20.

### Frame format (confirmed)

```
7E  [N]  [group]  [device]  [id bytes + data bytes, N total]  [checksum]
```

- `N` = byte count of **id + data** only (does **not** include group, device, or checksum)
- **Checksum** = `(13 + sum of all preceding bytes including 0x7E) % 256` ✓ verified on multiple packets
- Values are big-endian per the serial.yml documentation
- Multiple frames can be concatenated in a single USB bulk transfer

### Confirmed: heartbeat / device-ping (group 0x00)

Sent to every known device ID (18–30) roughly once per second. Payload length 0 — just the group and device bytes, no ID or data.

**Purpose:** Keep-alive / presence check. *(Concrete)*

### Confirmed: dash-send-telemetry (group 0x41, device 0x17, id [0xFD, 0xDE])

Matches `dash-send-telemetry` from `serial.yml` exactly (write group 65, id [253, 222], 4 bytes int).

- Sent ~100× per second throughout the entire capture
- **Data is always `00 00 00 00` — never changes**, even while driving

**Best guess:** This may be a mode/enable flag rather than a live value. Value `0` = telemetry active (or "use default mode"). The actual per-frame telemetry is sent via group 0x43 below. *Needs further research — what does a non-zero value do?*

### Confirmed: main real-time telemetry (group 0x43, device 0x17, id [0x7D, 0x23])

Sent ~17–20× per second. This is the primary live data stream pithouse sends to the wheel/dash.

Full frame example (paused):
```
7E 18 43 17  7D 23  32 00 23 32 02 20  00 30 08 A6 09 01 58 B9 28 E1 01 00 50 63 00 00  FC
             └─id─┘ └───fixed 6B──────┘ └──────────── 16 bytes of live data ────────────┘ chk
```

The 6 bytes after the id (`32 00 23 32 02 20`) are **constant** across all captured packets — likely sub-command or mode metadata.

The remaining **16 bytes change during driving**. Full dataset: 447 packets over ~20 seconds.

| Offset (in 16B block) | Paused value | Behavior during driving | Unique values | Notes |
|---|---|---|---|---|
| 0 | `00` | mostly 0, occasional bursts of non-zero | 21 | |
| 1 | `30` | rapidly changing, full byte range | 89 | |
| 2 | `08` | rapidly changing, full byte range | 122 | |
| 3 | `A6` | monotonically incrementing, rolls over 0xFF→0x00 | 168 | low byte of a counter |
| 4 | `09` | carries byte [3] rollover: increments `09`→`0A` | 5 | high byte of same counter |
| 5 | `01` | toggles `01` ↔ `81` (bit 7 only) | 4 | ToggleBit7; actual value = 1 throughout |
| 6 | `58` | varies; range 1–248 | 41 | |
| 7 | `B9` | varies; range 65–185 | 42 | |
| 8 | `28` | near-constant; 4 unique values total | 4 | |
| 9 | `E1` | alternates `E1` ↔ `61` in multi-second chunks | 3 | ToggleBit7; actual value = 0x61 = 97 (constant) |
| 10 | `01` | varies; ToggleBit7 active | 42 | |
| 11 | `00` | rapidly changing, wide range | 98 | |
| 12 | `50` | varies; ToggleBit7 active | 22 | |
| 13 | `63` | monotonically increases over full capture, rolls past 0x7F | 78 | |
| 14 | `00` | low-variation; multiples of `0x10` | 24 | |
| 15 | `00` | low-variation | 24 | |

#### Capture context

- **Session type:** Practice (not race) — GAP, and possibly all lap times, may be absent/zero
- **Car state when paused:** Moving (speed was non-zero, telemetry frozen in-place by AC)
- **Car state when unpaused:** Continued moving immediately
- **Some values may zero during pause** (unconfirmed which ones)
- RPM LED (`group 0x3F`) showed **50.1% at pause** — engine running at idle RPM mid-drive

#### Active dashboard profile (m Formula 1.mzdash)

The Pithouse dashboard active during the capture uses exactly **16 unique telemetry channels**, matching the 16-byte block size. Channels in order of first appearance in the profile file:

| Index | Channel | Type | Screen(s) |
|---|---|---|---|
| 0 | `SpeedKmh` | float, km/h | 0, 2 |
| 1 | `Throttle` | float, 0–1 | 0 |
| 2 | `Brake` | float, 0–1 | 0 |
| 3 | `Gear` | int, −1/0/1–8 | 0, 2 |
| 4 | `Rpm` | int | 0 |
| 5 | `CurrentLapTime` | float, seconds | 1 |
| 6 | `LastLapTime` | float, seconds | 1 |
| 7 | `BestLapTime` | float, seconds | 1 |
| 8 | `GAP` | float, seconds | 1 |
| 9 | `FuelRemainder` | float, 0–100% | 2 |
| 10 | `ErsState` | float, 0–100% | 2 |
| 11 | `TyreWearFrontLeft` | float, 0–100% | 2 |
| 12 | `TyreWearFrontRight` | float, 0–100% | 2 |
| 13 | `TyreWearRearLeft` | float, 0–100% | 2 |
| 14 | `TyreWearRearRight` | float, 0–100% | 2 |
| 15 | `DrsState` | int, 0/1 | 2 |

A different dashboard profile would use different channels and produce a different byte layout — the 16-byte block is **not a fixed global schema**.

The channel-index order above does **not** appear to map directly to byte positions in the 16-byte block; the byte-to-channel mapping is determined by something not yet identified in the protocol (possibly a fixed firmware layout, or a separate config command).

#### Proposed byte mapping (partial, needs controlled verification)

| Byte | Paused | Unique | Behavior | Proposed channel | Confidence |
|---|---|---|---|---|---|
| 0 | `00` | 21 | 0 most of the time; non-zero bursts during aggressive driving (~t=4–5.5 s) | Throttle or Brake (whichever zeroes on pause) | Low |
| 1 | `30` | 89 | Rapidly changing, full range | Unknown | — |
| 2 | `08` | 122 | Rapidly changing, full range | Unknown | — |
| 3 | `A6` | 168 | Monotonically incrementing, rolls 0xFF→0x00 | **24-bit counter low byte** (not a game channel) | High |
| 4 | `09` | 5 | Carries byte [3] overflow; `09`→`0A` at one rollover | **24-bit counter mid byte** | High |
| 5 | `01` | 4 | ToggleBit7; actual value = 1 throughout | **24-bit counter high byte** | Medium |
| 6 | `58` | 41 | Moderate variation | Unknown | — |
| 7 | `B9` | 42 | Moderate variation | Unknown | — |
| 8 | `28` | 4 | Near-constant; 4 unique values: `28`, `4F`, `60`, `86` | **Gear** — high nibble may encode gear (2, 4, 6, 8 observed → gears 1–4 in ×2 encoding?) | Medium |
| 9 | `E1` | 3 | ToggleBit7; actual value = **97** constant throughout | **FuelRemainder (97%)** or **BestLapTime (97 s = 1:37)** | Medium |
| 10 | `01` | 42 | ToggleBit7 active, varies | Unknown | — |
| 11 | `00` | 98 | **0 when paused**; rises linearly with RPM during driving; zeroes again at final pause | **Rpm** — rises ~0x70 (0%) to ~0xB5 (100% of redline); ToggleBit7 active above 0x7F | Medium-High |
| 12 | `50` | 22 | ToggleBit7 active, moderate variation | Unknown | — |
| 13 | `63` | 78 | **Non-zero when paused (0x63 = 99)**; oscillates with driving — peaks ~148, troughs ~78 | **SpeedKmh** — 99 km/h frozen when paused, rises/falls with track speed | Medium |
| 14 | `00` | 24 | Low-variation; multiples of `0x10`; 0 when paused | Unknown | — |
| 15 | `00` | 24 | Low-variation; 0 when paused | **DrsState** (0 = off; rarely changes) | Low |

**Key reasoning:**
- **Byte [13] = SpeedKmh**: Non-zero when paused (car was moving, AC freezes telemetry), oscillates with corner entry/exit (99→148→78→148 km/h over the capture), does not zero on pause. The raw byte value in km/h is plausible for a medium-speed track section.
- **Bytes [3..5] = counter**: Byte [3] increments monotonically at ~8.5 Hz independent of game state, carrying into [4] then [5]. This is likely a Pithouse-side frame/sync counter embedded in the protocol, not a game channel. It happens to fill 3 of the 16 byte slots.
- **Byte [8] = Gear**: Only 4 unique values across the full 447-packet capture — consistent with a gear that changed 3 times. The high nibble of the 4 observed values is 2, 4, 6, 8 (all even), suggesting a ×2-encoded gear (1→`0x2_`, 2→`0x4_`, 3→`0x6_`, 4→`0x8_`). Encoding not confirmed.
- **Byte [9] = constant 97**: Practice session with car on track → fuel and ERS barely changed. Both FuelRemainder=97% and BestLapTime=97 s (1:37) are plausible; a second capture where either changes would disambiguate.
- **Byte [11] = Rpm**: Cross-referenced against the RPM LED (group 0x3F) which sends current_rpm/max_rpm × 1023 at ~1 Hz. Raw byte [11] rises smoothly from ~0x70 at 0% RPM to ~0xB5 at 100% RPM as the car accelerates, matching every RPM LED level change with a ~50–150 ms lead. ToggleBit7 is active: values cross 0x7F, so the apparent "stripped" correlation inverts above the midpoint. The byte zeros at final pause (t=17.654) while the RPM LED (updated 1 Hz) still shows 88.3% — this reflects that the main telemetry sources from the game engine (which freezes/zeroes on pause) while the RPM LED is driven independently by Pithouse. Approximate encoding: `rpm_fraction = (raw - 0x70) / 0x45`, range 0x70–0xB5.

**Unresolved:**
- Bytes [1], [2], [6], [7], [10], [12], [14] — no confident assignment yet
- Whether channel mzdash-index order has any relationship to byte order
- Exact gear encoding (nibble vs full byte vs some other packing)
- Which of FuelRemainder / BestLapTime occupies byte [9]
- Whether bytes [3..5] truly represent a protocol counter or encode one of the `*LapTime` channels

### Confirmed: wheel RPM LED telemetry (group 0x3F, device 0x17, id [0x1A, 0x00])

This goes to the **steering wheel** (device 23), not the dash. The wheel's RPM LED bar is a separate subsystem from the dash display — the group 0x43 main telemetry and these wheel LED commands are independent paths.

Sent ~once per second. Matches `wheel-send-rpm-telemetry` from the database. 8 bytes = 4 × 16-bit LE values.

Structure: `[current_pos, 0x0000, 0x03FF, 0x0000]`

- Values 2 and 4 are **always 0**.
- Value 3 is **always 1023 (0x03FF)** — fixed max/denominator of the 10-bit scale.
- Value 1 tracks the current RPM position on a **0–1023 (10-bit) scale**, proportional to RPM:

| Time | Value 1 | % of max | Notes |
|------|---------|----------|-------|
| 0.135 (paused) | 513 | 50.1% | sitting at idle while paused |
| 2.088 | 771 | 75.4% | accelerating after unpause |
| 2.779 | 975 | 95.3% | higher RPM |
| 3.093 | 1023 | 100.0% | at/near redline |
| 3.495 | 0 | 0% | drop — gear shift? |

The paused value of 513 (50%) is the RPM the car was at the moment the game was paused — Assetto Corsa freezes telemetry values in place when paused rather than resetting to idle. This is consistent and expected.

**Confirmed:** This is `current_rpm / max_rpm * 1023` — current RPM as a 10-bit fraction of redline. Value 3 (always 1023) is the fixed denominator. Assetto Corsa sends both current and max RPM to Pithouse, which scales them for the wheel.

### Confirmed: sequence counter (group 0x2D, device 0x13, id [0xF5, 0x31])

Sent ~50× per second. Group 45 is not in the known serial.yml command list.

- Data: `00 00 00 XX` where XX increments by 1 each send (`0x9F` → `0xEA` over the ~17s capture)
- Starts incrementing immediately, including during paused state
- Device 0x13 = base (device 19)

**Best guess:** A monotonic sequence/frame counter or timestamp sent to the base unit. May be used for synchronization or to detect dropped packets. *Group 45 purpose unknown — needs further research.*

### Other group 0x43 sub-commands (device 0x17)

| ID | Data | Frequency | Notes |
|---|---|---|---|
| `[0xFC, 0x00]` | 3 bytes, e.g. `01 72 00`, `09 0C 01`, `03 66 00` | ~once per 5s | **Best guess:** periodic status/config write. Third byte increments. |
| `[0x7C, 0x00]` | 25 bytes | ~once per 60s | Rare. Possibly a settings block. |

### Group 0x43 broadcast (device 0x14 = dash, 0x15 = unknown)

Short (length=2) packets sent to device 20 (dash) and device 21 — payload is just `[group][device]` with no id/data. Sent every ~5 seconds in groups.

**Best guess:** Heartbeat or device-sync signal specific to these peripherals. *Purpose unclear.*

### Group 0x0E poll (host → device 0x13, ~1 Hz)

Pithouse sends a 3-byte poll to the base device once per second: `00 01 XX` where `XX` is the low byte of a 16-bit BE countdown counter starting at 0x013a (314). The counter decrements by 1 each send toward zero. The base device (response group 0x8E, device 0x31) echoes the 3-byte payload and appends 4 extra bytes whose meaning is unknown (varies each response; occasionally has bit 15 set on byte [1]).

Note: a separate one-shot poll `00 00 01` is sent to the wheel (device 0x17) at connection. This shares the group number with the unsolicited ASCII debug logs the wheel pushes, but is a distinct request/response exchange.

### Group 0x1F (host → device 0x12, ~3 Hz)

3-byte payload: `4F XX 00/01` where `XX` cycles through values `08`, `09`, `0A`, `0B`. Device responds (group 0x9F, device 0x21) mirroring the two bytes and inserting `0xFF` as a status byte: `4F XX FF 00/01`. Purpose unknown.

### Group 0x40 (host → device 0x17, ~3.4 Hz)

Always `28 02 01 00`. Wheel always responds (group 0xC0, device 0x71) with `28 02 00 00` — same first two bytes, third byte flipped 01→00. Sent throughout the entire capture at a constant rate. Possibly a keep-alive or secondary telemetry channel enable. Distinct from group 0x41 (`dash-send-telemetry`) which uses a different payload.

### Group 0x2B (host → device 0x13, occasional)

3-byte payload `02 00 00`, sent only 3 times across the 20-second capture (at t ≈ 15, 18, 20 s). Device echoes back (group 0xAB, device 0x31). Timing suggests it may be sent when Pithouse detects a state change (second pause, session end). Purpose unknown.

### Wheel connection probe sequence

When a wheel is detected, Pit House sends the following identity queries to device 0x17 (wheel, ID 23). Responses arrive asynchronously, matched by group. All identity strings are 16-byte null-padded ASCII.

| Group | Cmd ID | Response content | Notes |
|-------|--------|-----------------|-------|
| 0x02 | — | 1-byte value (observed: `0x02`) | Unknown — possibly protocol version |
| 0x04 | `0x00` + 3 zero bytes | 2 bytes | Unknown |
| 0x05 | `0x00` + 3 zero bytes | 4 bytes, differs per model | Possibly capability flags or button/LED count. VGS: `01 02 1f 01`; CS V2.1: `01 02 26 00` |
| 0x07 | `0x01` | 16-byte string | **Model name** — e.g. `VGS`, `CS V2.1`, `R5 Black # MOT-1` |
| 0x08 | `0x01` | 16-byte string | **Hardware version** — e.g. `RS21-W08-HW SM-C` |
| 0x08 | `0x02` | 16-byte string | **Hardware revision** — e.g. `U-V12`, `U-V02` |
| 0x0f | `0x01` | 16-byte string | **Firmware (SW) version** — e.g. `RS21-W08-MC SW` |
| 0x10 | `0x00` | 16-byte string | **Serial number, first half** |
| 0x10 | `0x01` | 16-byte string | **Serial number, second half** |
| 0x11 | `0x04` | 2 bytes | Unknown |

The full serial number is the two halves concatenated (32 ASCII characters). Observed across wheel models: R5 Black (old protocol, ES series) also responds correctly to all of these.

For identity read requests, the request payload is just the command ID byte with no value bytes appended. The device responds with 16 bytes regardless.

### Telemetry color chunks (wheel LED effects)

RPM and button LED colors are sent via the `wheel-telemetry-rpm-colors` and `wheel-telemetry-button-colors` commands. Each command has a fixed payload size of 20 bytes, so colors are split into 20-byte chunks and sent as multiple consecutive writes.

Each LED is encoded as 4 bytes:

| Byte | Meaning |
|------|---------|
| 0 | LED index (0-based) |
| 1 | Red |
| 2 | Green |
| 3 | Blue |

Five LEDs fit per chunk (5 × 4 = 20 bytes). With 10 RPM LEDs this is exactly 2 chunks. With 14 button LEDs it is 3 chunks, where the last chunk only contains 4 real LEDs (16 bytes) and must be padded to 20 bytes.

**Padding caveat:** zero-padding produces `[0x00, 0x00, 0x00, 0x00]`, which the firmware interprets as a valid entry: *set LED index 0 to RGB(0, 0, 0)*. This overwrites the correct color already set in the first chunk and causes button 0 to flicker black on every color send. The workaround is to use index `0xFF` for any unused padding entries, which the firmware ignores.
