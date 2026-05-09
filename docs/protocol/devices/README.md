# Devices — index and command tables

Per-device pages with device IDs, group breakdowns, and full command tables. For frame format / checksum / wire-level rules see [`../wire/`](../wire/). For functional cross-cuts (telemetry, sessions, dashboards, LEDs) see the sibling folders under [`../`](../).

## Protocol constants

| Name | Dec | Hex | Notes |
|------|-----|-----|-------|
| Frame start byte | 126 | `0x7E` | First byte of every frame |
| Checksum magic | 13 | `0x0D` | Added to the running byte sum before mod 256 |

## Device IDs

All communication goes over a single COM serial interface. Device IDs are addresses on the internal bus — the wheelbase hub routes each frame to the correct peripheral.

| Device | Dec | Hex | File | Notes |
|--------|-----|-----|------|-------|
| main / hub | 18 | `0x12` | [`main-hub-0x12.md`](main-hub-0x12.md) | Base USB address; hub enumeration shares this ID. Identity bytes are byte-identical to dev `0x13` (see [`../identity/hub-base-cascade.md`](../identity/hub-base-cascade.md)) |
| base | 19 | `0x13` | [`wheelbase-0x13.md`](wheelbase-0x13.md) | Wheelbase motor controller |
| dash | 20 | `0x14` | [`dash-0x14.md`](dash-0x14.md) | Standalone Moza MDD display peripheral |
| wheel | 21 | `0x15` | — | Secondary wheel address — observed in group `0x43` broadcasts, purpose unclear |
| wheel | 23 | `0x17` | [`wheel-0x17.md`](wheel-0x17.md) | Primary steering wheel address used by all known models. Identity probes in [`../identity/`](../identity/) |
| pedals | 25 | `0x19` | [`pedals-0x19.md`](pedals-0x19.md) | Pedal set. Identity quirks in [`../identity/pedal-0x19.md`](../identity/pedal-0x19.md) |
| hpattern / sequential | 26 | `0x1A` | [`shifter-0x1A.md`](shifter-0x1A.md) | H-pattern and sequential shifter share this device ID; distinguish via `shifter-type` setting |
| handbrake | 27 | `0x1B` | [`handbrake-0x1B.md`](handbrake-0x1B.md) | |
| estop | 28 | `0x1C` | [`estop-0x1C.md`](estop-0x1C.md) | Emergency stop button |
| AB9 active shifter | — | — | [`ab9-shifter.md`](ab9-shifter.md) | Separate USB device. Settings sent via `Group 0x1F → dev 0x12` |

Response device IDs have their nibbles swapped: base `0x13` → response `0x31`, wheel `0x17` → `0x71`, etc. Response group IDs have `0x80` added. See [`../wire/frame-format.md`](../wire/frame-format.md) for full response encoding rules.

## Cross-cutting commands (all devices)

- EEPROM direct access (group `0x0A`) — applicable to any device, see [`../settings/eeprom-0x0A.md`](../settings/eeprom-0x0A.md)

## Command table format

Each device section is organized by group. Within a group, the **ID** column shows the command ID bytes (1–4 bytes, big-endian hex). **Bytes** is the payload size (value bytes only, not the ID). **Dir** is `R` (read-only), `W` (write-only), or `RW` (both, using the same group).

When a command is truly read/write and uses the same ID in both directions, the group is shown as `read / write`. When read and write use the same group number, `Dir` disambiguates.

## Authoritative source

`Pit House/bin/rs21_parameter.db` — SQLite DB with 919 commands across 23 groups. Device-side tables here are derived from that DB and from USB capture analysis. See [`../telemetry/service-parameter-transforms.md`](../telemetry/service-parameter-transforms.md) for value-encoding rules.
