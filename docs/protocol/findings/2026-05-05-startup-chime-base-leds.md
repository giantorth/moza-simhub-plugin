# 2026-05-05 — Startup chime & base ambient LED captures

## Captures analysed

| File | Content |
|------|---------|
| `usb-capture/startupchime/Wheel Base Chimes part 1.pcapng` | PitHouse cycling through chimes 1–5 (set index → preview) |
| `usb-capture/startupchime/Wheel Base Chimes Part 2.pcapng` | PitHouse cycling through chimes 6–10 |
| `usb-capture/startupchime/Wheel Base Chimes Settings.pcapng` | Enable/disable chime + volume 0↔255 |
| `usb-capture/startupchime/Moza R25 Wheel Base Settings Part 1.pcapng` | Full LED settings read + standby mode cycling (0–4) |
| `usb-capture/startupchime/Wheel Base Settings Part 2.pcapng` | Standby mode 5 (flow) + mode 0 (constant) |

Hardware: R25 wheel base.

## Key findings

### Startup chime

- 10 built-in chimes, indexed 1–10.
- PitHouse sets index then immediately previews: `music-index-set(N)` → `music-preview(N)`.
- Volume 0x00–0xFF, default 0x17 (23). Max observed 0xFF.
- Enable/disable is a separate toggle from index selection.
- All commands get echo ACK (same payload on response group 0xAA).

### Base ambient LEDs — settings

- First capture evidence of these commands on hardware. Previously DB-only.
- 6 standby modes (0–5). Mode 1 exists in PitHouse but name/effect unknown.
- Each mode has its own interval register, written via `1E [mode] [u16be ms]`.
- Brightness uses cmd `1F FF` on wire (DB says `1F 02`).
- Write commands produce dual response: `0xA0` ACK + `0xA2` read-notify.
- Default state on R25: rainbow mode, brightness 100, startup/shutdown color #66B8FF.

### Base ambient LEDs — live RPM telemetry

- PitHouse drives the base LEDs as an RPM bar during game telemetry.
- Uses cmd `0x1A` (live-color-chunk) and `0x1B` (live-bitmask) on group `0x20` device `0x12` — analogous to wheel LED live commands (`0x19`/`0x1A` on group `0x3F` device `0x17`).
- Each strip (0 and 1) is independently addressable — 9 LEDs each, 18 total. PitHouse sends identical data to both in this capture but that is an application choice, not a protocol constraint.
- Bitmask sent every frame (~10 Hz); colors only re-sent on palette change.
- RPM color gradient: green → yellow → red → magenta (over-rev).
- Group `0x1F` (31) polled alongside: 4 status registers (`4F 08/09/0A/0B`, all return `FF 00`), init queries (`0A`, `0F`), brightness readback (`4D` → `64` = 100).

## Canonical docs updated

- [`../leds/base-ambient-0x20-0x22.md`](../leds/base-ambient-0x20-0x22.md) — full rewrite with capture data, live telemetry section
- [`../devices/wheelbase-0x13.md`](../devices/wheelbase-0x13.md) § Group 0x2A — enriched with semantics and examples
