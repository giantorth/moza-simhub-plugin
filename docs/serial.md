# Moza Racing Serial Command Reference — REDIRECT INDEX

> **This file has moved.** Per-device command tables now live under [`docs/protocol/devices/`](protocol/devices/README.md). Cross-cutting EEPROM access lives at [`docs/protocol/settings/eeprom-0x0A.md`](protocol/settings/eeprom-0x0A.md).
>
> Section anchors below are preserved (the original H2/H3 headers remain) so existing deep links from source code, sibling docs, and external bookmarks continue to resolve. Each section now contains a one-line pointer to its new home.

Start here: [`docs/protocol/devices/README.md`](protocol/devices/README.md).

---

## Protocol Constants
→ [`protocol/devices/README.md`](protocol/devices/README.md#protocol-constants)

## Device IDs
→ [`protocol/devices/README.md`](protocol/devices/README.md#device-ids)

## Command Table Format
→ [`protocol/devices/README.md`](protocol/devices/README.md#command-table-format)

## EEPROM Direct Access (Group `0x0A` / 10 — any device)
→ [`protocol/settings/eeprom-0x0A.md`](protocol/settings/eeprom-0x0A.md)

## Main (Device `0x12` / 18)
→ [`protocol/devices/main-hub-0x12.md`](protocol/devices/main-hub-0x12.md)

### Group `0x1E` (30) — Output (read-only)
→ [`protocol/devices/main-hub-0x12.md`](protocol/devices/main-hub-0x12.md)

### Group `0x1F` (31) — Settings
→ [`protocol/devices/main-hub-0x12.md`](protocol/devices/main-hub-0x12.md)

### Group `0x20` / `0x22` (32 / 34) — Base Ambient LEDs
→ [`protocol/devices/main-hub-0x12.md`](protocol/devices/main-hub-0x12.md) · also see [`protocol/leds/base-ambient-0x20-0x22.md`](protocol/leds/base-ambient-0x20-0x22.md)

## Pedals (Device `0x19` / 25)
→ [`protocol/devices/pedals-0x19.md`](protocol/devices/pedals-0x19.md) · identity quirks at [`protocol/identity/pedal-0x19.md`](protocol/identity/pedal-0x19.md)

### Group `0x23` / `0x24` (35 / 36) — Settings
→ [`protocol/devices/pedals-0x19.md`](protocol/devices/pedals-0x19.md)

### Group `0x25` (37) — Output (read-only)
→ [`protocol/devices/pedals-0x19.md`](protocol/devices/pedals-0x19.md)

### Group `0x26` (38) — Calibration (write-only)
→ [`protocol/devices/pedals-0x19.md`](protocol/devices/pedals-0x19.md)

## Wheelbase (Device `0x13` / 19)
→ [`protocol/devices/wheelbase-0x13.md`](protocol/devices/wheelbase-0x13.md)

### Group `0x28` / `0x29` (40 / 41) — Settings
→ [`protocol/devices/wheelbase-0x13.md`](protocol/devices/wheelbase-0x13.md)

### Group `0x2A` (42) — Calibration / Music
→ [`protocol/devices/wheelbase-0x13.md`](protocol/devices/wheelbase-0x13.md)

### Group `0x2B` (43) — Status (read-only)
→ [`protocol/devices/wheelbase-0x13.md`](protocol/devices/wheelbase-0x13.md)

### Group `0x2D` (45) — Sequence Counter (write-only)
→ [`protocol/devices/wheelbase-0x13.md`](protocol/devices/wheelbase-0x13.md) · prose at [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

## Standalone Dash Display — MDD (Device `0x14` / 20)
→ [`protocol/devices/dash-0x14.md`](protocol/devices/dash-0x14.md)

### Group `0x32` / `0x33` (50 / 51) — Settings
→ [`protocol/devices/dash-0x14.md`](protocol/devices/dash-0x14.md)

## Steering Wheel (Device `0x17` / 23)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) · identity probes at [`protocol/identity/`](protocol/identity/)

### Identity Queries (read-only)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) · prose at [`protocol/identity/wheel-probe-sequence.md`](protocol/identity/wheel-probe-sequence.md)

### Group `0x3F` / `0x40` (63 / 64) — Configuration
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md)

### Group `0x3F` (63) — Live Telemetry (write-only)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) · LED encoding at [`protocol/leds/color-commands.md`](protocol/leds/color-commands.md)

### Group `0x41` (65) — Telemetry Enable (write-only)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) · prose at [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

### Group `0x43` (67) — Live Telemetry Stream (write-only)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) · prose at [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

### Old-Protocol Commands (Groups `0x3F` / `0x40`)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md)

### Extended LED Group Architecture (Groups `0x3F` / `0x40`)
→ [`protocol/devices/wheel-0x17.md`](protocol/devices/wheel-0x17.md) · group breakdown at [`protocol/leds/wheel-groups-0x3F-0x40.md`](protocol/leds/wheel-groups-0x3F-0x40.md)

## H-Pattern Shifter (Device `0x1A` / 26)
→ [`protocol/devices/shifter-0x1A.md`](protocol/devices/shifter-0x1A.md)

### Group `0x51` / `0x52` (81 / 82) — Settings
→ [`protocol/devices/shifter-0x1A.md`](protocol/devices/shifter-0x1A.md)

### Group `0x53` (83) — Output (read-only)
→ [`protocol/devices/shifter-0x1A.md`](protocol/devices/shifter-0x1A.md)

### Group `0x54` (84) — Calibration (write-only)
→ [`protocol/devices/shifter-0x1A.md`](protocol/devices/shifter-0x1A.md)

## Sequential Shifter (Device `0x1A` / 26)
→ [`protocol/devices/shifter-0x1A.md`](protocol/devices/shifter-0x1A.md) (merged with H-pattern; same device ID)

## Handbrake (Device `0x1B` / 27)
→ [`protocol/devices/handbrake-0x1B.md`](protocol/devices/handbrake-0x1B.md)

### Group `0x5B` / `0x5C` (91 / 92) — Settings
→ [`protocol/devices/handbrake-0x1B.md`](protocol/devices/handbrake-0x1B.md)

### Group `0x5D` (93) — Output (read-only)
→ [`protocol/devices/handbrake-0x1B.md`](protocol/devices/handbrake-0x1B.md)

### Group `0x5E` (94) — Calibration (write-only)
→ [`protocol/devices/handbrake-0x1B.md`](protocol/devices/handbrake-0x1B.md)

## E-Stop (Device `0x1C` / 28)
→ [`protocol/devices/estop-0x1C.md`](protocol/devices/estop-0x1C.md)

## Hub (Device `0x12` / 18)
→ [`protocol/devices/main-hub-0x12.md`](protocol/devices/main-hub-0x12.md) (merged with main; same device ID)

### Group `0x64` (100) — Connected Device Status (read-only)
→ [`protocol/devices/main-hub-0x12.md`](protocol/devices/main-hub-0x12.md)
